using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent;
using ANGI.Domain.Entities;
using FluentValidation;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Agent.CreateConversation;

/// <summary>Implements AGENT-01 without creating an empty official roadmap.</summary>
public sealed class CreateAgentConversationUseCase(IValidator<CreateAgentConversationRequestDto> validator,
    IAgentRepository repository, IRoadmapRepository roadmaps, ICurrentUserService user, IUnitOfWork unitOfWork,
    TimeProvider clock, AgentLimits limits) : ICreateAgentConversationUseCase
{
    /// <summary>Validates ownership, derives available trip values and persists expiring short-term state.</summary>
    public async Task<AgentConversationDto> ExecuteAsync(CreateAgentConversationRequestDto request, CancellationToken ct)
    {
        // STEP-01: Validate the request.
        await validator.ValidateAndThrowAsync(request, ct);
        // STEP-02: Prepare userId through AgentConversationRules.UserId, state as AgentState and ownedRoadmap from roadmaps.GetOwnedDetailAsync.
        var userId = AgentConversationRules.UserId(user);
        var state = new AgentState();
        var ownedRoadmap = request.RoadmapId is { } ownedId
            ? await roadmaps.GetOwnedDetailAsync(ownedId, userId, ct)
                ?? throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.") : null;
        // STEP-03: Open the database transaction.
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        // STEP-04: When request.RoadmapId exists, run the branch below.
        if (request.RoadmapId is { } id)
        {
            // STEP-05: Load repository.LockRoadmapConversationAsync.
            await repository.LockRoadmapConversationAsync(id, ct);
            // STEP-06: Set state.
            state = AgentContextBuilder.StateFromRoadmap(ownedRoadmap!);
            // STEP-07: Load existing through repository.GetByRoadmapAsync.
            var existing = await repository.GetByRoadmapAsync(id, userId, ct);
            // STEP-08: When existing exists, run the branch below.
            if (existing is not null)
            {
                // STEP-09: Set existing.
                existing = await repository.LockOwnedAsync(existing.Id, userId, ct) ?? throw AgentConversationRules.Missing();
                // STEP-10: Prepare now from clock.GetUtcNow.
                var now = clock.GetUtcNow().UtcDateTime;
                // STEP-11: When AgentConversationRules.Busy(existing, now) does not hold and more.
                if (!AgentConversationRules.Busy(existing, now) &&
                    (existing.Status != AgentConversationStatus.Active || existing.ExpiresAt <= now))
                {
                    // STEP-12: When existing.CurrentProposalId exists, prepare old through repository.GetProposalAsync and check whether old.Status is AgentProposalStatus.Ready.
                    if (existing.CurrentProposalId is { } oldId)
                    {
                        var old = await repository.GetProposalAsync(existing.Id, oldId, ct);
                        if (old?.Status == AgentProposalStatus.Ready) old.Status = AgentProposalStatus.Expired;
                    }
                    // STEP-13: Update existing: State, CurrentProposalId, CurrentProposal and Status.
                    existing.State = AgentJson.Write(state with { HistoryStartSequence = (long)existing.LastMessageSequence + 1 });
                    existing.CurrentProposalId = null; existing.CurrentProposal = null;
                    existing.Status = AgentConversationStatus.Active;
                    // STEP-14: Call AgentConversationRules.ClearLease.
                    AgentConversationRules.ClearLease(existing);
                    // STEP-15: Increment existing.Version.
                    // STEP-16: Update existing: ExpiresAt.
                    existing.Version++; existing.ExpiresAt = now.AddDays(limits.ActiveDays);
                    // STEP-17: Save the pending changes.
                    await unitOfWork.SaveChangesAsync(ct);
                }
                // STEP-18: Prepare current, then load history through repository.GetCompletedHistoryAsync.
                var current = AgentJson.Read<AgentState>(existing.State);
                var history = await repository.GetCompletedHistoryAsync(existing.Id, limits.MaxHistoryMessages, ct);
                // STEP-19: Prepare proposal from repository.GetProposalSnapshotAsync.
                var proposal = existing.CurrentProposalId is { } currentId
                    ? await repository.GetProposalSnapshotAsync(existing.Id, currentId, ct) : null;
                // STEP-20: Commit the transaction.
                await transaction.CommitAsync(ct);
                // STEP-21: Return the new result.
                return new(existing.Id, existing.Version, AgentJson.Name(existing.Status), current,
                    AgentConversationRules.Busy(existing, now), history.Where(x => x.Role != AgentMessageRole.Tool &&
                        x.SequenceNo >= (current.HistoryStartSequence ?? 0)).Select(x => new AgentTranscript(
                            AgentJson.Name(x.Role), AgentJson.Read<AgentStoredMessage>(x.Payload).Content)).ToArray(),
                    proposal is null ? null : AgentContextBuilder.MapProposal(proposal)) { Reused = true };
            }
        }
        // STEP-22: Prepare conversation as AgentConversation.
        var conversation = new AgentConversation { UserId = userId, RoadmapId = request.RoadmapId,
            State = AgentJson.Write(state), ExpiresAt = clock.GetUtcNow().UtcDateTime.AddDays(limits.ActiveDays) };
        // STEP-23: Queue conversation through repository.AddConversation.
        repository.AddConversation(conversation);
        // STEP-24: Save the pending changes.
        await unitOfWork.SaveChangesAsync(ct);
        // STEP-25: Commit the transaction.
        await transaction.CommitAsync(ct);
        // STEP-26: Return the new result.
        return new(conversation.Id, conversation.Version, "active", state, false, [], null);
    }
}
