using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.UseCases.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Agent.CleanupConversations;

/// <summary>Applies bounded short-term retention without cascading to official roadmap data.</summary>
public sealed class CleanupAgentConversationsUseCase(IAgentRepository repository, IUnitOfWork unitOfWork,
    TimeProvider clock, AgentLimits limits) : ICleanupAgentConversationsUseCase
{
    /// <summary>Expires idle conversations and unlinks current proposals before removing retention-aged children.</summary>
    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        // STEP-01: Prepare now from clock.GetUtcNow and cutoff through now.AddDays.
        var now = clock.GetUtcNow().UtcDateTime;
        var cutoff = now.AddDays(-limits.RetentionDays);
        // STEP-02: Open the database transaction.
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        // STEP-03: Load conversations through repository.LockCleanupBatchAsync.
        var conversations = await repository.LockCleanupBatchAsync(now, cutoff, ct);
        // STEP-04: Prepare removable through conversations.Where(filtered).ToArray.
        var removable = conversations.Where(x => x.UpdatedAt <= cutoff && x.RoadmapId is null).ToArray();
        // STEP-05: Go through each conversation in conversations.
        foreach (var conversation in conversations)
        {
            // STEP-06: Update conversation: MaintenanceWrite.
            conversation.MaintenanceWrite = true;
            // STEP-07: Prepare expired.
            var expired = conversation.Status != AgentConversationStatus.Active || conversation.ExpiresAt <= now;
            // STEP-08: When expired is true and conversation.ProcessingTurnId exists, prepare abandoned through repository.GetTurnAsync and check whether abandoned exists.
            if (expired && conversation.ProcessingTurnId is { } turn)
            {
                var abandoned = await repository.GetTurnAsync(conversation.Id, turn, ct);
                if (abandoned is not null) abandoned.ProcessingStatus = AgentProcessingStatus.Failed;
            }
            // STEP-09: When expired is true, call AgentConversationRules.ClearLease, set conversation.Status and 1 more action(s).
            if (expired)
            {
                AgentConversationRules.ClearLease(conversation);
                conversation.Status = AgentConversationStatus.Expired;
                conversation.Version++;
            }
            // STEP-10: When conversation.UpdatedAt <= cutoff, set conversation.CurrentProposal, set conversation.CurrentProposalId and 1 more action(s).
            if (conversation.UpdatedAt <= cutoff)
            {
                conversation.CurrentProposal = null;
                conversation.CurrentProposalId = null;
                if (conversation.RoadmapId.HasValue) conversation.State = "{}";
            }
        }
        // STEP-11: Save the pending changes.
        // Unlink the restrictive circular pointer before deleting proposals in the same transaction.
        await unitOfWork.SaveChangesAsync(ct);
        // STEP-12: For each conversation in conversations, check whether removable contains conversation.
        foreach (var conversation in conversations)
        {
            if (removable.Contains(conversation)) await repository.RemoveConversationAsync(conversation, ct);
            else await repository.PruneAsync(conversation, cutoff, ct);
        }
        // STEP-13: Save the pending changes.
        await unitOfWork.SaveChangesAsync(ct);
        // STEP-14: Commit the transaction.
        await transaction.CommitAsync(ct);
        // STEP-15: Return conversations.Length.
        return conversations.Length;
    }
}
