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
        await validator.ValidateAndThrowAsync(request, ct);
        var userId = AgentConversationRules.UserId(user);
        var state = new AgentState();
        var ownedRoadmap = request.RoadmapId is { } ownedId
            ? await roadmaps.GetOwnedDetailAsync(ownedId, userId, ct)
                ?? throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.") : null;
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        if (request.RoadmapId is { } id)
        {
            await repository.LockRoadmapConversationAsync(id, ct);
            state = AgentContextBuilder.StateFromRoadmap(ownedRoadmap!);
            var existing = await repository.GetByRoadmapAsync(id, userId, ct);
            if (existing is not null)
            {
                existing = await repository.LockOwnedAsync(existing.Id, userId, ct) ?? throw AgentConversationRules.Missing();
                var now = clock.GetUtcNow().UtcDateTime;
                if (!AgentConversationRules.Busy(existing, now) &&
                    (existing.Status != AgentConversationStatus.Active || existing.ExpiresAt <= now))
                {
                    if (existing.CurrentProposalId is { } oldId)
                    {
                        var old = await repository.GetProposalAsync(existing.Id, oldId, ct);
                        if (old?.Status == AgentProposalStatus.Ready) old.Status = AgentProposalStatus.Expired;
                    }
                    existing.State = AgentJson.Write(state with { HistoryStartSequence = (long)existing.LastMessageSequence + 1 });
                    existing.CurrentProposalId = null; existing.CurrentProposal = null;
                    existing.Status = AgentConversationStatus.Active;
                    AgentConversationRules.ClearLease(existing);
                    existing.Version++; existing.ExpiresAt = now.AddDays(limits.ActiveDays);
                    await unitOfWork.SaveChangesAsync(ct);
                }
                var current = AgentJson.Read<AgentState>(existing.State);
                var history = await repository.GetCompletedHistoryAsync(existing.Id, limits.MaxHistoryMessages, ct);
                var proposal = existing.CurrentProposalId is { } currentId
                    ? await repository.GetProposalSnapshotAsync(existing.Id, currentId, ct) : null;
                await transaction.CommitAsync(ct);
                return new(existing.Id, existing.Version, AgentJson.Name(existing.Status), current,
                    AgentConversationRules.Busy(existing, now), history.Where(x => x.Role != AgentMessageRole.Tool &&
                        x.SequenceNo >= (current.HistoryStartSequence ?? 0)).Select(x => new AgentTranscript(
                            AgentJson.Name(x.Role), AgentJson.Read<AgentStoredMessage>(x.Payload).Content)).ToArray(),
                    proposal is null ? null : AgentContextBuilder.MapProposal(proposal)) { Reused = true };
            }
        }
        var conversation = new AgentConversation { UserId = userId, RoadmapId = request.RoadmapId,
            State = AgentJson.Write(state), ExpiresAt = clock.GetUtcNow().UtcDateTime.AddDays(limits.ActiveDays) };
        repository.AddConversation(conversation);
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(conversation.Id, conversation.Version, "active", state, false, [], null);
    }
}
