using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.UseCases.Agent;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.Services.Agent;

/// <summary>Shares deterministic proposal validation and application between explicit API and fenced chat saves.</summary>
public sealed class AgentProposalCommitter(IAgentRepository repository, IRoadmapRepository roadmaps,
    IUnitOfWork unitOfWork, AgentProposalValidator validator, TimeProvider clock)
{
    /// <summary>Checks the owned revision and calculates routes outside the transaction that applies the proposal.</summary>
    public async Task<AgentCommitPreparation> PrepareAsync(AgentConversation conversation, int userId,
        ConfirmAgentProposalRequestDto request, bool ownsTurn, CancellationToken ct)
    {
        var proposal = await repository.GetProposalSnapshotAsync(conversation.Id, request.ProposalId, ct) ?? throw NotCommittable();
        RequireVersion(proposal, request.ProposalVersion);
        if (proposal.Status == AgentProposalStatus.Committed)
            return new(conversation.Id, proposal.Id, proposal.ProposalVersion, null, AgentJson.Read<AgentCommitDto>(proposal.CommitResult!));
        RequireCommittable(conversation, proposal, clock.GetUtcNow().UtcDateTime, ownsTurn);
        var snapshot = AgentJson.Read<AgentSnapshot>(proposal.Payload);
        var source = snapshot.BaseRoadmapId is { } sourceId
            ? await roadmaps.GetOwnedDetailAsync(sourceId, userId, ct) ?? throw RoadmapMissing() : null;
        if (source is not null && source.UpdatedAt != snapshot.BaseRoadmapUpdatedAt) throw RoadmapConflict();
        var outside = await validator.ValidateAsync(snapshot, source is null ? null : AgentJson.FromRoadmap(source, snapshot.State), true, true, ct);
        if (outside.Validation.Errors.Length > 0) throw new AgentProposalValidationException(outside.Validation);
        return new(conversation.Id, proposal.Id, proposal.ProposalVersion, snapshot);
    }

    /// <summary>Applies under the caller's conversation lock and transaction; the caller persists transcript and commits.</summary>
    public async Task<(AgentCommitDto Result, bool Applied)> CommitLockedAsync(AgentConversation conversation,
        AgentCommitPreparation preparation, int userId, bool ownsTurn, CancellationToken ct)
    {
        if (conversation.Id != preparation.ConversationId || conversation.UserId != userId) throw NotCommittable();
        var proposal = await repository.GetProposalAsync(conversation.Id, preparation.ProposalId, ct) ?? throw NotCommittable();
        RequireVersion(proposal, preparation.ProposalVersion);
        if (proposal.Status == AgentProposalStatus.Committed) return (AgentJson.Read<AgentCommitDto>(proposal.CommitResult!), false);
        var now = clock.GetUtcNow().UtcDateTime;
        RequireCommittable(conversation, proposal, now, ownsTurn);
        var snapshot = preparation.Snapshot ?? throw NotCommittable();
        AgentSnapshot? source = null;
        Domain.Entities.Roadmap roadmap;
        if (snapshot.BaseRoadmapId is { } roadmapId)
        {
            roadmap = await roadmaps.LockAndGetTrackedAsync(roadmapId, userId, ct) ?? throw RoadmapMissing();
            if (roadmap.UpdatedAt != snapshot.BaseRoadmapUpdatedAt) throw RoadmapConflict();
            source = AgentJson.FromRoadmap(roadmap, snapshot.State);
        }
        else
        {
            roadmap = new Domain.Entities.Roadmap { UserId = userId };
            roadmaps.Add(roadmap);
        }
        await repository.LockCatalogAsync(snapshot.Items.Select(item => item.DishId).Distinct().ToArray(), ct);
        var current = await validator.ValidateAsync(snapshot, source, true, false, ct);
        if (current.Validation.Errors.Length > 0) throw new AgentProposalValidationException(current.Validation);
        var mapping = Apply(roadmap, current.Snapshot);
        AgentCommittedImpressionReporter.Enqueue(repository, current.Snapshot, proposal.Id, userId, now);
        await unitOfWork.SaveChangesAsync(ct);
        var updatedAt = await repository.GetRoadmapUpdatedAtAsync(roadmap.Id, ct);
        var result = new AgentCommitDto(roadmap.Id, updatedAt, proposal.Id, mapping.ToDictionary(pair => pair.Key, pair => pair.Value.Id));
        proposal.Status = AgentProposalStatus.Committed;
        proposal.ResultRoadmapId = roadmap.Id;
        proposal.ResultRoadmapUpdatedAt = updatedAt;
        proposal.CommitResult = AgentJson.Write(result);
        proposal.CommittedAt = now;
        conversation.RoadmapId = roadmap.Id;
        conversation.State = AgentJson.Write(snapshot.State with { FocusDay = null, FocusMeal = null,
            PendingQuestion = null, PendingStateChange = null, SourceRoadmapUpdatedAt = updatedAt });
        return (result, true);
    }

    /// <summary>Retains item identity only for unchanged dishes so replacement never transfers earlier feedback.</summary>
    private Dictionary<string, RoadmapItem> Apply(Domain.Entities.Roadmap roadmap, AgentSnapshot snapshot)
    {
        var state = snapshot.State;
        roadmap.Title = state.Title.Trim();
        roadmap.StartDate = state.StartDate;
        roadmap.NumDays = state.NumDays!.Value;
        roadmap.BudgetAmount = state.BudgetAmount;
        roadmap.OriginMode = state.OriginMode switch { "pinned" => RoadmapOriginMode.Pinned, "gps" => RoadmapOriginMode.Gps, _ => RoadmapOriginMode.None };
        roadmap.OriginLatitude = state.OriginMode == "pinned" ? state.OriginLatitude : null;
        roadmap.OriginLongitude = state.OriginMode == "pinned" ? state.OriginLongitude : null;
        roadmap.OriginLabel = state.OriginMode == "pinned" ? state.OriginLabel : null;
        var existing = roadmap.Days.SelectMany(day => day.Items).ToDictionary(item => item.Id);
        var retained = snapshot.Items.Where(item => item.SourceItemId.HasValue).Select(item => item.SourceItemId!.Value).ToHashSet();
        foreach (var item in existing.Values.Where(item => !retained.Contains(item.Id))) roadmaps.RemoveItem(item);
        foreach (var day in roadmap.Days.Where(day => day.DayNumber > roadmap.NumDays).ToArray()) roadmap.Days.Remove(day);
        for (short day = 1; day <= roadmap.NumDays; day++)
            if (roadmap.Days.All(existingDay => existingDay.DayNumber != day)) roadmap.Days.Add(new RoadmapDay { DayNumber = day, Roadmap = roadmap });
        var mapping = new Dictionary<string, RoadmapItem>();
        foreach (var item in snapshot.Items)
        {
            var day = roadmap.Days.Single(day => day.DayNumber == item.DayNumber);
            var saved = item.SourceItemId is { } id && existing.TryGetValue(id, out var old) && old.DishId == item.DishId ? old : null;
            if (saved is null)
            {
                saved = new RoadmapItem { RoadmapDay = day, DishId = item.DishId, Source = RoadmapItemSource.Ai, RecoRequestId = item.RequestId };
                roadmaps.AddItem(saved);
            }
            saved.RoadmapDay = day;
            saved.MealSlot = Enum.Parse<MealSlot>(item.MealSlot.Replace("_", ""), true);
            saved.SortOrder = item.SortOrder;
            saved.RecoRequestId = item.RequestId;
            mapping.Add(item.ItemKey, saved);
        }
        return mapping;
    }

    /// <summary>Checks the displayed proposal revision before either first application or committed replay.</summary>
    private static void RequireVersion(AgentProposal proposal, int version)
    {
        if (proposal.ProposalVersion != version) throw new ConflictException("PROPOSAL_VERSION_CONFLICT", "Phiên bản đề xuất không khớp.");
    }

    /// <summary>Allows only current ready proposals with resolved questions and unchanged reviewed constraints.</summary>
    private static void RequireCommittable(AgentConversation conversation, AgentProposal proposal, DateTime now, bool ownsTurn)
    {
        AgentConversationRules.RequireActive(conversation, now);
        if (!ownsTurn && AgentConversationRules.Busy(conversation, now)) throw new ConflictException("CONVERSATION_BUSY", "Hội thoại đang xử lý tin nhắn.");
        if (proposal.ExpiresAt <= now) throw new GoneException("PROPOSAL_EXPIRED", "Đề xuất đã hết hạn.");
        if (proposal.Status != AgentProposalStatus.Ready || conversation.CurrentProposalId != proposal.Id) throw NotCommittable();
        var state = AgentJson.Read<AgentState>(conversation.State);
        if (state.PendingQuestion is not null || state.PendingStateChange is not null ||
            !AgentConversationRules.SameConstraints(state, AgentJson.Read<AgentSnapshot>(proposal.Payload).State)) throw NotCommittable();
    }

    /// <summary>Reports an official roadmap change that requires a fresh reviewed proposal.</summary>
    private static ConflictException RoadmapConflict() => new("ROADMAP_UPDATED_AT_CONFLICT", "Roadmap đã thay đổi. Cần tạo đề xuất mới.");
    /// <summary>Conceals unavailable or foreign proposal references behind the standard confirmation failure.</summary>
    private static ConflictException NotCommittable() => new("PROPOSAL_NOT_COMMITTABLE", "Đề xuất không còn sẵn sàng để xác nhận.");
    /// <summary>Reports an unavailable owned source roadmap.</summary>
    private static NotFoundException RoadmapMissing() => new("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");
}
