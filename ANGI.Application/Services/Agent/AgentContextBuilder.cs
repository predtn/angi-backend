using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.Services.Agent;

/// <summary>Loads trusted state and current snapshots rather than reconstructing a plan from chat text.</summary>
public sealed class AgentContextBuilder(IAgentRepository repository, IRoadmapRepository roadmaps, AgentLimits limits,
    TimeProvider clock)
{
    /// <summary>Loads completed bounded history and the latest official roadmap after ownership checks.</summary>
    public async Task<AgentContext> BuildAsync(AgentConversation conversation, SendAgentMessageRequestDto request, CancellationToken ct)
    {
        var state = request.ConfirmedState ?? AgentJson.Read<AgentState>(conversation.State);
        var roadmap = conversation.RoadmapId is { } id
            ? await roadmaps.GetOwnedDetailAsync(id, conversation.UserId, ct) ?? throw RoadmapMissing()
            : null;
        var proposal = conversation.CurrentProposalId is { } proposalId
            ? await repository.GetProposalSnapshotAsync(conversation.Id, proposalId, ct) : null;
        AgentSnapshot? source = null;
        if (proposal?.Status == AgentProposalStatus.Ready && proposal.ExpiresAt > clock.GetUtcNow().UtcDateTime)
        {
            source = AgentJson.Read<AgentSnapshot>(proposal.Payload);
            if (source.BaseRoadmapVersion.HasValue && source.BaseRoadmapVersion != roadmap?.Version)
                throw new ConflictException("ROADMAP_VERSION_CONFLICT", "Roadmap đã thay đổi. Hãy bỏ bản chỉnh sửa cũ và tạo đề xuất mới.");
        }
        else if (roadmap is not null)
        {
            if (request.ConfirmedState is null) state = RefreshRoadmapMetadata(state, roadmap);
            source = AgentJson.FromRoadmap(roadmap, state);
        }
        var history = await repository.GetCompletedHistoryAsync(conversation.Id, limits.MaxHistoryMessages, ct);
        return new AgentContext
        {
            ConversationId = conversation.Id, UserId = conversation.UserId, State = state, Source = source,
            ProposalVersion = proposal?.Status == AgentProposalStatus.Ready ? proposal.ProposalVersion : null,
            Roadmap = roadmap, Request = request,
            History = history.Where(x => x.Role != AgentMessageRole.Tool).Select(x => new AgentTranscript(
                AgentJson.Name(x.Role), AgentJson.Read<AgentStoredMessage>(x.Payload).Content)).ToList()
        };
    }

    /// <summary>Preserves manual metadata edits when a new draft starts from an already committed roadmap.</summary>
    private static AgentState RefreshRoadmapMetadata(AgentState state, Domain.Entities.Roadmap roadmap)
    {
        var current = StateFromRoadmap(roadmap);
        var retainGps = current.OriginMode == "gps" && state.OriginMode == "gps";
        var latitude = retainGps ? state.OriginLatitude : current.OriginLatitude;
        var longitude = retainGps ? state.OriginLongitude : current.OriginLongitude;
        var focus = state.FocusDay <= current.NumDays ? state.FocusDay : null;
        return state with
        {
            Title = current.Title, StartDate = current.StartDate, NumDays = current.NumDays,
            BudgetAmount = current.BudgetAmount, BudgetUnrestricted = current.BudgetAmount is null && state.BudgetUnrestricted,
            Province = state.Province ?? current.Province, OriginMode = current.OriginMode,
            OriginLatitude = latitude, OriginLongitude = longitude, OriginLabel = current.OriginLabel,
            RadiusKm = latitude.HasValue ? state.RadiusKm : null, FocusDay = focus, FocusMeal = focus.HasValue ? state.FocusMeal : null
        };
    }

    /// <summary>Initializes only values present in an owned roadmap; an ambiguous province stays unknown.</summary>
    public static AgentState StateFromRoadmap(Domain.Entities.Roadmap roadmap)
    {
        var provinces = roadmap.Days.SelectMany(x => x.Items).Select(x => x.Dish.Restaurant?.ProvinceName)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new AgentState
        {
            Province = provinces.Length == 1 ? provinces[0] : null, Title = roadmap.Title, NumDays = roadmap.NumDays,
            StartDate = roadmap.StartDate, BudgetAmount = roadmap.BudgetAmount, OriginMode = AgentJson.Name(roadmap.OriginMode),
            OriginLatitude = roadmap.OriginLatitude, OriginLongitude = roadmap.OriginLongitude, OriginLabel = roadmap.OriginLabel
        };
    }

    /// <summary>Maps an immutable persisted snapshot into the documented preview response.</summary>
    public static AgentProposalDto MapProposal(AgentProposal proposal) => new(proposal.Id, proposal.ProposalVersion,
        AgentJson.Name(proposal.Operation), AgentJson.Name(proposal.Status), AgentJson.Read<AgentSnapshot>(proposal.Payload),
        AgentJson.Read<AgentValidation>(proposal.ValidationResult), proposal.ExpiresAt);

    /// <summary>Returns the existing ownership-safe roadmap error.</summary>
    private static NotFoundException RoadmapMissing() => new("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");
}

/// <summary>Safe stored message content and per-attempt admission timestamps used by persistent quotas.</summary>
public sealed record AgentStoredMessage(string Content, DateTime[]? Attempts = null, string? SelectedItemKey = null,
    string? Command = null, AgentState? ConfirmedState = null);
