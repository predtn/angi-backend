using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent.Intake;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.Services.Agent;

/// <summary>Loads trusted state and current snapshots rather than reconstructing a plan from chat text.</summary>
public sealed class AgentContextBuilder(IAgentRepository repository, IRoadmapRepository roadmaps, AgentLimits limits,
    TimeProvider clock, ITagRepository tags, AgentPreferenceService? preferences = null)
{
    /// <summary>Loads completed bounded history and the latest official roadmap after ownership checks.</summary>
    public async Task<AgentContext> BuildAsync(AgentConversation conversation, SendAgentMessageRequestDto request, CancellationToken ct)
    {
        // STEP-01: Prepare stored through AgentJson.Read, state, roadmap from RoadmapMissing, proposal from repository.GetProposalSnapshotAsync and 1 more.
        var stored = AgentJson.Read<AgentState>(conversation.State);
        var state = request.ConfirmedState is { } confirmed
            ? confirmed with { SourceRoadmapUpdatedAt = stored.SourceRoadmapUpdatedAt, HistoryStartSequence = stored.HistoryStartSequence,
                Preferences = stored.Preferences, PendingPlanningRequest = stored.PendingPlanningRequest, PendingSave = null, DishOffered = null, PlanningIntake = stored.PlanningIntake,
                DayAreas = stored.DayAreas, PendingDayArea = stored.PendingDayArea, NamedAreaLabel = stored.NamedAreaLabel, WidenedMeals = stored.WidenedMeals }
            : stored;
        // Roadmap đã gắn có thể bị xóa hay sửa tay ở trang lịch trong lúc chat còn bản nháp. Lượt này vẫn đi qua model thay vì trả 404/409
        // (UI không có nút bỏ nháp, nên lỗi sẽ khóa chat tới khi bản nháp hết hạn). Bản nháp dựng trên bản lịch cũ không còn lưu được:
        // nó bị bỏ khỏi context, FinishAsync đánh dấu Discarded, và model được báo attachedRoadmapDeleted hoặc roadmapChangedOutsideChat.
        var roadmap = conversation.RoadmapId is { } id ? await roadmaps.GetOwnedDetailAsync(id, conversation.UserId, ct) : null;
        var roadmapDeleted = conversation.RoadmapId is not null && roadmap is null;
        var proposal = conversation.CurrentProposalId is { } proposalId
            ? await repository.GetProposalSnapshotAsync(conversation.Id, proposalId, ct) : null;
        var outdated = proposal?.Status == AgentProposalStatus.Ready && AgentJson.Read<AgentSnapshot>(proposal.Payload) is { BaseRoadmapId: not null } based
            && (roadmap is null || based.BaseRoadmapUpdatedAt != roadmap.UpdatedAt);
        if (outdated) proposal = null;
        AgentSnapshot? source = null;
        // STEP-02: When proposal.Status is AgentProposalStatus.Ready and proposal.ExpiresAt > clock.GetUtcNow().UtcDateTime, set source and more.
        if (proposal?.Status == AgentProposalStatus.Ready && proposal.ExpiresAt > clock.GetUtcNow().UtcDateTime)
            source = AgentJson.Read<AgentSnapshot>(proposal.Payload);
        else if (roadmap is not null)
        {
            if (request.ConfirmedState is null && state.SourceRoadmapUpdatedAt != roadmap.UpdatedAt) state = RefreshRoadmapMetadata(state, roadmap);
            source = AgentJson.FromRoadmap(roadmap, state);
        }
        // STEP-03: Load history through repository.GetCompletedHistoryAsync.
        var history = await repository.GetCompletedHistoryAsync(conversation.Id, limits.MaxHistoryMessages, ct);
        // STEP-04: When preferences exists and proposal.Status is not AgentProposalStatus.Ready, set state.
        // A legacy ready preview can still be committed unchanged. Other turns initialize lazily.
        if (preferences is not null && proposal?.Status != AgentProposalStatus.Ready)
            state = await preferences.EnsureAsync(state, conversation.UserId, ct);
        // STEP-05: Load catalogTags through tags.GetTagsAsync.
        var catalogTags = await tags.GetTagsAsync(null, ct);
        // STEP-06: Return a new AgentContext.
        return new AgentContext
        {
            ConversationId = conversation.Id, UserId = conversation.UserId, State = state, Source = source,
            ProposalVersion = proposal?.Status == AgentProposalStatus.Ready ? proposal.ProposalVersion : null,
            CurrentProposal = proposal is null ? null : MapProposal(proposal),
            Roadmap = roadmap, AttachedRoadmapDeleted = roadmapDeleted, OutdatedProposalDropped = outdated, Request = request,
            CatalogTags = catalogTags.Select(tag => (object)new { tag.Id, tag.Code, tag.Name, type = AgentJson.Name(tag.TagType) }).ToArray(),
            CatalogTagNames = catalogTags.ToDictionary(tag => tag.Id, tag => tag.Name),
            History = history.Where(x => x.Role != AgentMessageRole.Tool && x.SequenceNo >= (state.HistoryStartSequence ?? 0)).Select(x => new AgentTranscript(
                AgentJson.Name(x.Role), AgentJson.Read<AgentStoredMessage>(x.Payload).Content)).ToList()
        };
    }

    /// <summary>Preserves manual metadata edits when a new draft starts from an already committed roadmap.</summary>
    private static AgentState RefreshRoadmapMetadata(AgentState state, Domain.Entities.Roadmap roadmap)
    {
        // STEP-01: Prepare current through StateFromRoadmap, retainGps, latitude, longitude and 1 more.
        var current = StateFromRoadmap(roadmap);
        var retainGps = current.OriginMode == "gps" && state.OriginMode == "gps";
        var latitude = retainGps ? state.OriginLatitude : current.OriginLatitude;
        var longitude = retainGps ? state.OriginLongitude : current.OriginLongitude;
        var focus = state.FocusDay <= current.NumDays ? state.FocusDay : null;
        // STEP-02: Return a copy of state with updated fields.
        return state with
        {
            Title = current.Title, StartDate = current.StartDate, NumDays = current.NumDays,
            BudgetAmount = current.BudgetAmount, BudgetUnrestricted = current.BudgetAmount is null && state.BudgetUnrestricted,
            Province = state.Province ?? current.Province, OriginMode = current.OriginMode,
            OriginLatitude = latitude, OriginLongitude = longitude, OriginLabel = current.OriginLabel,
            RadiusKm = latitude.HasValue ? state.RadiusKm : null, FocusDay = focus, FocusMeal = focus.HasValue ? state.FocusMeal : null,
            DayAreas = state.DayAreas?.Where(x => x.DayNumber <= current.NumDays).ToArray() is { Length: > 0 } areas ? areas : null,
            WidenedMeals = state.WidenedMeals?.Where(x => x.DayNumber <= current.NumDays).ToArray() is { Length: > 0 } widened ? widened : null,
            SourceRoadmapUpdatedAt = roadmap.UpdatedAt
        };
    }

    /// <summary>
    /// A state the model returns never blanks a trip fact already known, such as the province, length or start date of the roadmap the chat
    /// was opened on: the model rewrites the whole state and often leaves out what the user did not repeat. A new value still replaces it.
    /// </summary>
    public static AgentState? KeepKnown(AgentState? proposed, AgentState current) => proposed is null ? null : proposed with
    {
        Province = string.IsNullOrWhiteSpace(proposed.Province) ? current.Province : proposed.Province,
        NumDays = proposed.NumDays ?? current.NumDays,
        StartDate = proposed.StartDate ?? current.StartDate,
        BudgetAmount = proposed.BudgetAmount ?? (proposed.BudgetUnrestricted ? null : current.BudgetAmount),
        Title = string.IsNullOrWhiteSpace(proposed.Title) || proposed.Title == new AgentState().Title ? current.Title : proposed.Title
    };

    /// <summary>
    /// Initializes only values present in an owned roadmap: its saved destination, else the one province of
    /// its dishes; an ambiguous province stays unknown.
    /// </summary>
    public static AgentState StateFromRoadmap(Domain.Entities.Roadmap roadmap)
    {
        var provinces = roadmap.Days.SelectMany(x => x.Items).Select(x => x.Dish.Restaurant?.ProvinceName)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new AgentState
        {
            Province = !string.IsNullOrWhiteSpace(roadmap.DestinationProvince) ? roadmap.DestinationProvince
                : provinces.Length == 1 ? provinces[0] : null,
            Title = roadmap.Title, NumDays = roadmap.NumDays,
            StartDate = roadmap.StartDate, BudgetAmount = roadmap.BudgetAmount, OriginMode = AgentJson.Name(roadmap.OriginMode),
            OriginLatitude = roadmap.OriginLatitude, OriginLongitude = roadmap.OriginLongitude, OriginLabel = roadmap.OriginLabel,
            SourceRoadmapUpdatedAt = roadmap.UpdatedAt
        };
    }

    /// <summary>Maps an immutable persisted snapshot into the documented preview response.</summary>
    public static AgentProposalDto MapProposal(AgentProposal proposal) => new(proposal.Id, proposal.ProposalVersion,
        AgentJson.Name(proposal.Operation), AgentJson.Name(proposal.Status), OrderedSnapshot(proposal.Payload),
        AgentJson.Read<AgentValidation>(proposal.ValidationResult), proposal.ExpiresAt);

    private static AgentSnapshot OrderedSnapshot(string payload)
    {
        var snapshot = AgentJson.Read<AgentSnapshot>(payload);
        return snapshot with { Items = AgentJson.OrderItems(snapshot.Items) };
    }
}

/// <summary>Safe stored message content and per-attempt admission timestamps used by persistent quotas.</summary>
public sealed record AgentStoredMessage(string Content, DateTime[]? Attempts = null, string? SelectedItemKey = null,
    string? Command = null, AgentState? ConfirmedState = null);
