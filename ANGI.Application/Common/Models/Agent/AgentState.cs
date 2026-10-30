using System.Text.Json;
using System.Text.Json.Serialization;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.Common.Models.Agent;

/// <summary>Versioned trip constraints; chat notes never change long-term taste or feedback.</summary>
public sealed record AgentState
{
    public int SchemaVersion { get; init; } = 1;
    public string? Province { get; init; }
    public short? NumDays { get; init; }
    public string Title { get; init; } = "Lịch trình ẩm thực";
    public DateOnly? StartDate { get; init; }
    public decimal? BudgetAmount { get; init; }
    public bool BudgetUnrestricted { get; init; }
    public decimal? MaxPricePerMeal { get; init; }
    public short[] TagIds { get; init; } = [];
    public short[] ExcludeTagIds { get; init; } = [];
    public string OriginMode { get; init; } = "none";
    public decimal? OriginLatitude { get; init; }
    public decimal? OriginLongitude { get; init; }
    public string? OriginLabel { get; init; }
    public decimal? RadiusKm { get; init; }
    public string? Note { get; init; }
    /// <summary>User targets only; the provider may propose these, but never the profile snapshot.</summary>
    public AgentTasteTarget[] TasteTargets { get; init; } = [];
    /// <summary>Day/meal overrides; omitted dimensions inherit the trip and profile.</summary>
    public AgentTasteOverride[] TasteOverrides { get; init; } = [];
    /// <summary>Catalog-backed selectors, separate from ontology tastes.</summary>
    public AgentMealRequest[] MealRequests { get; init; } = [];
    /// <summary>Reevaluate existing meals by default; future requires explicit user intent.</summary>
    public string PreferenceApplication { get; init; } = "replan";
    /// <summary>Declared coverage; empty preserves legacy or explicitly partial plans.</summary>
    public string[] MealSlots { get; init; } = [];
    /// <summary>Backend-owned snapshot initialized lazily, preserving legacy proposals until reranking.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentSessionPreferences? Preferences { get; init; }
    public string? PendingQuestion { get; init; }
    public short? FocusDay { get; init; }
    public string? FocusMeal { get; init; }
    /// <summary>Backend-staged constraints awaiting explicit consent in a subsequent chat turn.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentState? PendingStateChange { get; init; }
    /// <summary>Last observed exact UTC roadmap timestamp; unchanged timestamps preserve chat planning constraints.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? SourceRoadmapUpdatedAt { get; init; }
    /// <summary>First transcript sequence eligible for context after a fresh planning reset.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? HistoryStartSequence { get; init; }
}

/// <summary>One backend-priced itinerary item with stable proposal identity.</summary>
public sealed record AgentItem(string ItemKey, long? SourceItemId, short DayNumber, string MealSlot,
    short SortOrder, int DishId, Guid? RequestId = null, decimal Price = 0, string? DishName = null,
    string? RestaurantName = null, decimal? Latitude = null, decimal? Longitude = null,
    string? RankingFingerprint = null);

/// <summary>Backend-issued candidate provenance restricted to a day and meal.</summary>
public sealed record AgentCandidateGrant(short? DayNumber, string? MealSlot, int[] DishIds, Guid? RequestId,
    AgentState Constraints);

/// <summary>Full immutable itinerary and trusted provenance, independent of any saved roadmap.</summary>
public sealed record AgentSnapshot(AgentState State, AgentItem[] Items, AgentCandidateGrant[] Grants,
    long? BaseRoadmapId = null, DateTime? BaseRoadmapUpdatedAt = null, AgentChange[]? Changes = null, AgentScope? Scope = null,
    AgentItem[]? RetainedItems = null);

/// <summary>Explicit mutation scope; null day and meal mean the whole trip.</summary>
public sealed record AgentScope(short? DayNumber = null, string? MealSlot = null)
{
    /// <summary>Checks whether a proposed mutation belongs to this explicit scope.</summary>
    public bool Contains(AgentItem item) => (!DayNumber.HasValue || item.DayNumber == DayNumber) &&
        (MealSlot is null || item.MealSlot == MealSlot);

    /// <summary>A broad candidate search can authorize a narrower edit without widening its mutation boundary.</summary>
    public bool Covers(AgentScope other) => (!DayNumber.HasValue || DayNumber == other.DayNumber) &&
        (MealSlot is null || MealSlot == other.MealSlot);
}

/// <summary>Deterministic validation including route uncertainty and current total cost.</summary>
public sealed record AgentValidation(string[] Errors, string[] Warnings, decimal TotalCost, AgentDayRoute[] Routes);

/// <summary>Internal context loaded only after ownership checks.</summary>
public sealed class AgentContext
{
    public required long ConversationId { get; init; }
    public required int UserId { get; init; }
    public long? TurnId { get; set; }
    public long? AttemptId { get; set; }
    public required AgentState State { get; set; }
    public AgentSnapshot? Source { get; set; }
    public int? ProposalVersion { get; set; }
    public DTOs.Agent.AgentProposalDto? CurrentProposal { get; init; }
    public List<AgentScope> AlternativeScopes { get; } = [];
    public Domain.Entities.Roadmap? Roadmap { get; init; }
    public required DTOs.Agent.SendAgentMessageRequestDto Request { get; init; }
    public List<AgentTranscript> History { get; init; } = [];
    public List<AgentCandidateGrant> Grants { get; } = [];
    public List<AgentToolExchange> Exchanges { get; } = [];
    public string? RepairFeedback { get; set; }
    public bool ReplanAfterConfirmation { get; set; }
    public AgentState? ConstraintBaseline { get; set; }
    public object[] CatalogTags { get; set; } = [];
    /// <summary>Trusted tool failures/coverage hints used to construct the user-visible reply.</summary>
    public string? ToolNotice { get; set; }
}

/// <summary>Completed user and assistant text suitable for a bounded context window.</summary>
public sealed record AgentTranscript(string Role, string Content);

/// <summary>One allowlisted provider tool call with opaque provider correlation ID.</summary>
public sealed record AgentToolCall(string Id, string Name, JsonElement Arguments)
{
    /// <summary>Opaque provider correlation metadata, retained only within the current tool round-trip.</summary>
    [JsonIgnore]
    public JsonElement? ProviderExtraContent { get; init; }
}

/// <summary>In-memory tool round preserving provider call/result associations.</summary>
public sealed record AgentToolExchange(AgentToolCall[] Calls, JsonElement[] Results);

/// <summary>Provider-neutral LLM step, containing either tool requests or one structured result.</summary>
public sealed record AgentLlmStep(AgentToolCall[] Tools, AgentOutput? Output, int? Tokens = null, AgentLlmFailure? Failure = null)
{
    /// <summary>Creates a failed step that cannot be mistaken for malformed model output requiring repair.</summary>
    public static AgentLlmStep Failed(AgentLlmFailureKind kind, int? httpStatus = null, int? retryAfterSeconds = null, int? tokens = null) =>
        new([], null, tokens, new(kind, httpStatus, retryAfterSeconds));
}

/// <summary>Allowlisted output returned by the LLM; prices and provenance are never accepted here.</summary>
public sealed record AgentOutput
{
    public required string Action { get; init; }
    public required string Reply { get; init; }
    /// <summary>Allowlisted question intent; free provider text is never a factual response.</summary>
    public string? Clarification { get; init; }
    public AgentState? State { get; init; }
    public bool ConstraintsChangeConfirmed { get; init; }
    public int? BaseProposalVersion { get; init; }
    public AgentScope Scope { get; init; } = new();
    public AgentItem[] Items { get; init; } = [];
    public AgentChange[] Changes { get; init; } = [];
}

/// <summary>One bounded delta operation referencing a stable source item key.</summary>
public sealed record AgentChange(string Type, string? TargetItemKey, AgentItem? Item);

/// <summary>Serializes Agent data consistently and rejects unrecognized input fields.</summary>
public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Writes a structured snapshot or response as camel-case JSON.</summary>
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Reads a persisted versioned payload, rejecting missing root values.</summary>
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)
        ?? throw new JsonException("Missing Agent payload.");

    /// <summary>Returns the documented snake-case representation of an enum.</summary>
    public static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>Uses the same chronological order for stored previews, restored previews and route calculation.</summary>
    public static AgentItem[] OrderItems(IEnumerable<AgentItem> items) => items.OrderBy(item => item.DayNumber)
        .ThenBy(item => Array.IndexOf(UseCases.Validators.Agent.AgentStateValidator.Meals, item.MealSlot))
        .ThenBy(item => item.SortOrder).ThenBy(item => item.ItemKey, StringComparer.Ordinal).ToArray();

    /// <summary>Builds a proposal source from the current official aggregate while retaining item IDs.</summary>
    public static AgentSnapshot FromRoadmap(Domain.Entities.Roadmap roadmap, AgentState state) => new(state,
        roadmap.Days.SelectMany(day => day.Items.Select(item => new AgentItem(
            $"r{item.Id}", item.Id, day.DayNumber, Name(item.MealSlot), item.SortOrder, item.DishId,
            item.RecoRequestId, item.Dish.Price, item.Dish.Name, item.Dish.Restaurant?.Name,
            item.Dish.Restaurant?.Latitude, item.Dish.Restaurant?.Longitude))).ToArray(), [], roadmap.Id, roadmap.UpdatedAt);
}
