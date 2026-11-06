using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.UseCases.Agent;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.Services.Agent.Proposals;

/// <summary>Shares deterministic proposal validation and application between explicit API and fenced chat saves.</summary>
public sealed class AgentProposalCommitter(IAgentRepository repository, IRoadmapRepository roadmaps,
    IUnitOfWork unitOfWork, AgentProposalValidator validator, TimeProvider clock)
{
    private static readonly JsonSerializerOptions SnakeCase = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Checks the owned revision and calculates routes outside the transaction that applies the proposal.</summary>
    public async Task<AgentCommitPreparation> PrepareAsync(AgentConversation conversation, int userId,
        ConfirmAgentProposalRequestDto request, bool ownsTurn, CancellationToken ct)
    {
        // STEP-01: Prepare proposal from NotCommittable.
        var proposal = await repository.GetProposalSnapshotAsync(conversation.Id, request.ProposalId, ct) ?? throw NotCommittable();
        // STEP-02: Check the version rule through RequireVersion.
        RequireVersion(proposal, request.ProposalVersion);
        // STEP-03: Return the new result early when proposal.Status is AgentProposalStatus.Committed.
        if (proposal.Status == AgentProposalStatus.Committed)
            return new(conversation.Id, proposal.Id, proposal.ProposalVersion, null, AgentJson.Read<AgentCommitDto>(proposal.CommitResult!));
        // STEP-04: Check the committable rule through RequireCommittable.
        RequireCommittable(conversation, proposal, clock.GetUtcNow().UtcDateTime, ownsTurn);
        // STEP-05: Prepare snapshot through AgentJson.Read.
        var snapshot = AgentJson.Read<AgentSnapshot>(proposal.Payload);
        // STEP-06: When the computed value exists, set snapshot.
        // Ngày đi có thể được chốt sau khi bản nháp đã dựng; nó không đổi món nên không cần bản nháp mới.
        if ((request.StartDate ?? AgentJson.Read<AgentState>(conversation.State).StartDate) is { } startDate)
            snapshot = snapshot with { State = snapshot.State with { StartDate = startDate } };
        // STEP-07: Prepare source from RoadmapMissing.
        var source = snapshot.BaseRoadmapId is { } sourceId
            ? await roadmaps.GetOwnedDetailAsync(sourceId, userId, ct) ?? throw RoadmapMissing() : null;
        // STEP-08: Throw the error from RoadmapConflict when source exists and source.UpdatedAt is not snapshot.BaseRoadmapUpdatedAt.
        if (source is not null && source.UpdatedAt != snapshot.BaseRoadmapUpdatedAt) throw RoadmapConflict();
        // STEP-09: Run validator.ValidateAsync and keep outside.
        var outside = await validator.ValidateAsync(snapshot, source is null ? null : AgentJson.FromRoadmap(source, snapshot.State), true, !AgentScopedPlanning.IsLongTrip(snapshot.State), ct);
        // STEP-10: Throw AgentProposalValidationException when outside.Validation.Errors.Length > 0.
        if (outside.Validation.Errors.Length > 0) throw new AgentProposalValidationException(outside.Validation);
        // STEP-11: Return the new result.
        return new(conversation.Id, proposal.Id, proposal.ProposalVersion, snapshot);
    }

    /// <summary>Applies under the caller's conversation lock and transaction; the caller persists transcript and commits.</summary>
    public async Task<(AgentCommitDto Result, bool Applied)> CommitLockedAsync(AgentConversation conversation,
        AgentCommitPreparation preparation, int userId, bool ownsTurn, CancellationToken ct)
    {
        // STEP-01: Throw the error from NotCommittable when conversation.Id is not preparation.ConversationId or conversation.UserId is not userId.
        if (conversation.Id != preparation.ConversationId || conversation.UserId != userId) throw NotCommittable();
        // STEP-02: Prepare proposal from NotCommittable.
        var proposal = await repository.GetProposalAsync(conversation.Id, preparation.ProposalId, ct) ?? throw NotCommittable();
        // STEP-03: Check the version rule through RequireVersion.
        RequireVersion(proposal, preparation.ProposalVersion);
        // STEP-04: Return the collected values early when proposal.Status is AgentProposalStatus.Committed.
        if (proposal.Status == AgentProposalStatus.Committed) return (AgentJson.Read<AgentCommitDto>(proposal.CommitResult!), false);
        // STEP-05: Prepare now from clock.GetUtcNow.
        var now = clock.GetUtcNow().UtcDateTime;
        // STEP-06: Check the committable rule through RequireCommittable.
        RequireCommittable(conversation, proposal, now, ownsTurn);
        // STEP-07: Prepare snapshot from NotCommittable, source and roadmap.
        var snapshot = preparation.Snapshot ?? throw NotCommittable();
        AgentSnapshot? source = null;
        Domain.Entities.Roadmap roadmap;
        // STEP-08: When snapshot.BaseRoadmapId exists, set roadmap, throw the error from RoadmapConflict when roadmap.UpdatedAt is not snapshot.BaseRoadmapUpdatedAt and more.
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
        // STEP-09: Load repository.LockCatalogAsync.
        await repository.LockCatalogAsync(snapshot.Items.Select(item => item.DishId).Distinct().ToArray(), ct);
        // STEP-10: Run validator.ValidateAsync and keep current.
        var current = await validator.ValidateAsync(snapshot, source, true, false, ct);
        // STEP-11: Throw AgentProposalValidationException when current.Validation.Errors.Length > 0.
        if (current.Validation.Errors.Length > 0) throw new AgentProposalValidationException(current.Validation);
        // STEP-12: Prepare mapping through Apply.
        var mapping = Apply(roadmap, current.Snapshot);
        // STEP-13: Queue the items through EnqueueImpressions.
        EnqueueImpressions(repository, current.Snapshot, proposal.Id, userId, now);
        // STEP-14: Save the pending changes.
        await unitOfWork.SaveChangesAsync(ct);
        // STEP-15: Load updatedAt through repository.GetRoadmapUpdatedAtAsync.
        var updatedAt = await repository.GetRoadmapUpdatedAtAsync(roadmap.Id, ct);
        // STEP-16: Prepare result as AgentCommitDto.
        var result = new AgentCommitDto(roadmap.Id, updatedAt, proposal.Id, mapping.ToDictionary(pair => pair.Key, pair => pair.Value.Id));
        // STEP-17: Set proposal.Status, proposal.ResultRoadmapId, proposal.ResultRoadmapUpdatedAt, proposal.CommitResult and 3 more.
        proposal.Status = AgentProposalStatus.Committed;
        proposal.ResultRoadmapId = roadmap.Id;
        proposal.ResultRoadmapUpdatedAt = updatedAt;
        proposal.CommitResult = AgentJson.Write(result);
        proposal.CommittedAt = now;
        conversation.RoadmapId = roadmap.Id;
        conversation.State = AgentJson.Write(snapshot.State with { FocusDay = null, FocusMeal = null,
            PendingQuestion = null, PendingStateChange = null, PendingPlanningRequest = null, PendingSave = null, SourceRoadmapUpdatedAt = updatedAt });
        // STEP-18: Return the collected values.
        return (result, true);
    }

    /// <summary>Persists committed impressions atomically; the outbox worker sends them after commit.</summary>
    public static void EnqueueImpressions(IAgentRepository repository, AgentSnapshot snapshot, long proposalId, int userId, DateTime committedAt)
    {
        foreach (var group in snapshot.Items.Where(x => x.RequestId.HasValue).GroupBy(x => x.RequestId!.Value))
            foreach (var batch in group.Select(x => x.DishId).Distinct().Chunk(200))
                repository.AddOutboxMessage(new OutboxMessage
                {
                    EventType = OutboxEventType.AgentImpressionRecorded,
                    AggregateKey = $"user:{userId}",
                    Payload = JsonSerializer.Serialize(new { UserId = userId, RequestId = group.Key,
                        DishIds = batch, ShownAt = committedAt, AgentProposalId = proposalId }, SnakeCase),
                    Status = OutboxStatus.Pending
                });
    }

    /// <summary>Retains item identity only for unchanged dishes so replacement never transfers earlier feedback.</summary>
    private Dictionary<string, RoadmapItem> Apply(Domain.Entities.Roadmap roadmap, AgentSnapshot snapshot)
    {
        // STEP-01: Prepare state.
        var state = snapshot.State;
        // STEP-02: Update roadmap: Title, StartDate, NumDays, BudgetAmount and 4 more.
        roadmap.Title = state.Title.Trim();
        roadmap.StartDate = state.StartDate;
        roadmap.NumDays = state.NumDays!.Value;
        roadmap.BudgetAmount = state.BudgetAmount;
        roadmap.OriginMode = state.OriginMode switch { "pinned" => RoadmapOriginMode.Pinned, "gps" => RoadmapOriginMode.Gps, _ => RoadmapOriginMode.None };
        roadmap.OriginLatitude = state.OriginMode == "pinned" ? state.OriginLatitude : null;
        roadmap.OriginLongitude = state.OriginMode == "pinned" ? state.OriginLongitude : null;
        roadmap.OriginLabel = state.OriginMode == "pinned" ? state.OriginLabel : null;
        // STEP-03: Prepare existing through roadmap.Days.SelectMany(filtered).ToDictionary and retained through Where(filtered).Select(filtered).ToHashSet.
        var existing = roadmap.Days.SelectMany(day => day.Items).ToDictionary(item => item.Id);
        var retained = snapshot.Items.Where(item => item.SourceItemId.HasValue).Select(item => item.SourceItemId!.Value).ToHashSet();
        // STEP-04: For each item in existing.Values.Where(filtered), call roadmaps.RemoveItem.
        foreach (var item in existing.Values.Where(item => !retained.Contains(item.Id))) roadmaps.RemoveItem(item);
        // STEP-05: For each day in roadmap.Days.Where(filtered).ToArray(), call roadmap.Days.Remove.
        foreach (var day in roadmap.Days.Where(day => day.DayNumber > roadmap.NumDays).ToArray()) roadmap.Days.Remove(day);
        // STEP-06: While day <= roadmap.NumDays, check whether roadmap.Days.All matches the filter.
        for (short day = 1; day <= roadmap.NumDays; day++)
            if (roadmap.Days.All(existingDay => existingDay.DayNumber != day)) roadmap.Days.Add(new RoadmapDay { DayNumber = day, Roadmap = roadmap });
        // STEP-07: Prepare mapping as Dictionary<string, RoadmapItem>.
        var mapping = new Dictionary<string, RoadmapItem>();
        // STEP-08: Go through each item in snapshot.Items.
        foreach (var item in snapshot.Items)
        {
            // STEP-09: Prepare day through roadmap.Days.Single and saved from existing.TryGetValue.
            var day = roadmap.Days.Single(day => day.DayNumber == item.DayNumber);
            var saved = item.SourceItemId is { } id && existing.TryGetValue(id, out var old) && old.DishId == item.DishId ? old : null;
            // STEP-10: When saved is missing, set saved and call roadmaps.AddItem.
            if (saved is null)
            {
                saved = new RoadmapItem { RoadmapDay = day, DishId = item.DishId, Source = RoadmapItemSource.Ai, RecoRequestId = item.RequestId };
                roadmaps.AddItem(saved);
            }
            // STEP-11: Update saved: RoadmapDay, MealSlot, SortOrder and RecoRequestId.
            saved.RoadmapDay = day;
            saved.MealSlot = Enum.Parse<MealSlot>(item.MealSlot.Replace("_", ""), true);
            saved.SortOrder = item.SortOrder;
            saved.RecoRequestId = item.RequestId;
            // STEP-12: Add item.ItemKey to mapping.
            mapping.Add(item.ItemKey, saved);
        }
        // STEP-13: Return mapping.
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
        // STEP-01: Check the active rule through AgentConversationRules.RequireActive.
        AgentConversationRules.RequireActive(conversation, now);
        // STEP-02: Reject with CONVERSATION_BUSY when ownsTurn is false and AgentConversationRules.Busy(conversation, now) holds.
        if (!ownsTurn && AgentConversationRules.Busy(conversation, now)) throw new ConflictException("CONVERSATION_BUSY", "Hội thoại đang xử lý tin nhắn.");
        // STEP-03: Reject with PROPOSAL_EXPIRED when proposal.ExpiresAt <= now.
        if (proposal.ExpiresAt <= now) throw new GoneException("PROPOSAL_EXPIRED", "Đề xuất đã hết hạn.");
        // STEP-04: Throw the error from NotCommittable when proposal.Status is not AgentProposalStatus.Ready or conversation.CurrentProposalId is not proposal.Id.
        if (proposal.Status != AgentProposalStatus.Ready || conversation.CurrentProposalId != proposal.Id) throw NotCommittable();
        // STEP-05: Prepare state through AgentJson.Read.
        var state = AgentJson.Read<AgentState>(conversation.State);
        // STEP-06: Throw the error from NotCommittable when state.PendingQuestion exists, state.PendingStateChange exists, state.PendingPlanningRequest exists or 1 more condition(s).
        if (state.PendingQuestion is not null || state.PendingStateChange is not null || state.PendingPlanningRequest is not null ||
            !AgentConversationRules.SameConstraints(state, AgentJson.Read<AgentSnapshot>(proposal.Payload).State)) throw NotCommittable();
    }

    /// <summary>Reports an official roadmap change that requires a fresh reviewed proposal.</summary>
    private static ConflictException RoadmapConflict() => new("ROADMAP_UPDATED_AT_CONFLICT", "Lịch vừa thay đổi. Bạn nhờ mình lập lại trước khi lưu nhé.");
    /// <summary>Conceals unavailable or foreign proposal references behind the standard confirmation failure.</summary>
    private static ConflictException NotCommittable() => new("PROPOSAL_NOT_COMMITTABLE", "Đề xuất không còn sẵn sàng để xác nhận.");
    /// <summary>Reports an unavailable owned source roadmap.</summary>
    private static NotFoundException RoadmapMissing() => new("ROADMAP_NOT_FOUND", "Không tìm thấy lịch này.");
}
