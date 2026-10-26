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
    public string? PendingQuestion { get; init; }
    public short? FocusDay { get; init; }
    public string? FocusMeal { get; init; }
}

/// <summary>One backend-priced itinerary item with stable proposal identity.</summary>
public sealed record AgentItem(string ItemKey, long? SourceItemId, short DayNumber, string MealSlot,
    short SortOrder, int DishId, Guid? RequestId = null, decimal Price = 0, string? DishName = null,
    string? RestaurantName = null, decimal? Latitude = null, decimal? Longitude = null);

/// <summary>Backend-issued candidate provenance restricted to a day and meal.</summary>
public sealed record AgentCandidateGrant(short? DayNumber, string? MealSlot, int[] DishIds, Guid? RequestId,
    AgentState Constraints);

/// <summary>Full immutable itinerary and trusted provenance, independent of any saved roadmap.</summary>
public sealed record AgentSnapshot(AgentState State, AgentItem[] Items, AgentCandidateGrant[] Grants,
    long? BaseRoadmapId = null, long? BaseRoadmapVersion = null, AgentChange[]? Changes = null, AgentScope? Scope = null,
    AgentItem[]? RetainedItems = null);

/// <summary>Explicit mutation scope; null day and meal mean the whole trip.</summary>
public sealed record AgentScope(short? DayNumber = null, string? MealSlot = null)
{
    /// <summary>Checks whether a proposed mutation belongs to this explicit scope.</summary>
    public bool Contains(AgentItem item) => (!DayNumber.HasValue || item.DayNumber == DayNumber) &&
        (MealSlot is null || item.MealSlot == MealSlot);
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
    public Domain.Entities.Roadmap? Roadmap { get; init; }
    public required DTOs.Agent.SendAgentMessageRequestDto Request { get; init; }
    public List<AgentTranscript> History { get; init; } = [];
    public List<AgentCandidateGrant> Grants { get; } = [];
    public List<AgentToolExchange> Exchanges { get; } = [];
}

/// <summary>Completed user and assistant text suitable for a bounded context window.</summary>
public sealed record AgentTranscript(string Role, string Content);

/// <summary>One allowlisted provider tool call with opaque provider correlation ID.</summary>
public sealed record AgentToolCall(string Id, string Name, JsonElement Arguments);

/// <summary>In-memory tool round preserving provider call/result associations.</summary>
public sealed record AgentToolExchange(AgentToolCall[] Calls, JsonElement[] Results);

/// <summary>Provider-neutral LLM step, containing either tool requests or one structured result.</summary>
public sealed record AgentLlmStep(AgentToolCall[] Tools, AgentOutput? Output, int? Tokens = null);

/// <summary>Allowlisted output returned by the LLM; prices and provenance are never accepted here.</summary>
public sealed record AgentOutput
{
    public required string Action { get; init; }
    public required string Reply { get; init; }
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

    /// <summary>Builds a proposal source from the current official aggregate while retaining item IDs.</summary>
    public static AgentSnapshot FromRoadmap(Domain.Entities.Roadmap roadmap, AgentState state) => new(state,
        roadmap.Days.SelectMany(day => day.Items.Select(item => new AgentItem(
            $"r{item.Id}", item.Id, day.DayNumber, Name(item.MealSlot), item.SortOrder, item.DishId,
            item.RecoRequestId, item.Dish.Price, item.Dish.Name, item.Dish.Restaurant?.Name,
            item.Dish.Restaurant?.Latitude, item.Dish.Restaurant?.Longitude))).ToArray(), [], roadmap.Id, roadmap.Version);
}
