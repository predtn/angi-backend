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
        var now = clock.GetUtcNow().UtcDateTime;
        var cutoff = now.AddDays(-limits.RetentionDays);
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var conversations = await repository.LockCleanupBatchAsync(now, cutoff, ct);
        var removable = conversations.Where(x => x.UpdatedAt <= cutoff).ToArray();
        foreach (var conversation in conversations)
        {
            if (conversation.ProcessingTurnId is { } turn)
            {
                var abandoned = await repository.GetTurnAsync(conversation.Id, turn, ct);
                if (abandoned is not null) abandoned.ProcessingStatus = AgentProcessingStatus.Failed;
            }
            AgentConversationRules.ClearLease(conversation);
            conversation.Status = AgentConversationStatus.Expired;
            conversation.Version++;
            if (removable.Contains(conversation))
            {
                conversation.CurrentProposal = null;
                conversation.CurrentProposalId = null;
            }
        }
        // Unlink the restrictive circular pointer before deleting proposals in the same transaction.
        await unitOfWork.SaveChangesAsync(ct);
        foreach (var conversation in removable) await repository.RemoveConversationAsync(conversation, ct);
        if (removable.Length > 0) await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return conversations.Length;
    }
}
