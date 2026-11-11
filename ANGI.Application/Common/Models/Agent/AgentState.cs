using System.Text.Json;
using System.Text.Json.Serialization;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.Application.Services.Agent.Constraints;
using ANGI.Application.Services.Agent.Conversation;

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
    /// <summary>The user's own words stating a new budget, checked by AgentConstraintGuard and dropped before the state is kept.</summary>
    public string? BudgetQuote { get; init; }
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
    /// <summary>Backend-owned names the user already replaced or removed in this trip, so repeated "món khác" never cycles back to them.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? ReplacedDishNames { get; init; }
    /// <summary>Backend-owned meals the user removed on purpose; they may stay empty although their meal slot is declared.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentScope[]? SkippedMeals { get; init; }
    /// <summary>Backend-owned places from the user's itinerary; a day without one uses the area of the whole trip.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentDayArea[]? DayAreas { get; init; }
    /// <summary>No longer written, since the model asks which days move; it stays so conversations already stored can still be read.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentPlaceMatch? PendingDayArea { get; init; }
    /// <summary>Backend-owned label of a whole-trip area the user named; it tells that area apart from an origin picked on the map or by GPS.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NamedAreaLabel { get; init; }
    /// <summary>Backend-owned meals with no dish inside their named place, searched further out; only these meals may use the wider radius.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentWidenedMeal[]? WidenedMeals { get; init; }
    /// <summary>Backend-staged constraints awaiting explicit consent in a subsequent chat turn.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentState? PendingStateChange { get; init; }
    /// <summary>Backend-owned request retained while taste questions are unresolved; not yet confirmed trip state.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentPendingPlanningRequest? PendingPlanningRequest { get; init; }
    /// <summary>Backend-owned save question awaiting a reply; it never blocks the proposal and clients or the model cannot set it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentPendingSave? PendingSave { get; init; }
    /// <summary>Backend-owned mark that the last reply suggested a dish; it lives one turn so "ok" is read against that offer, and clients or the model cannot set it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DishOffered { get; init; }
    /// <summary>Backend-owned stage of the questions asked before a new plan ("details", "preferences"); clients and the model cannot set it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanningIntake { get; init; }
    /// <summary>Last observed exact UTC roadmap timestamp; unchanged timestamps preserve chat planning constraints.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? SourceRoadmapUpdatedAt { get; init; }
    /// <summary>First transcript sequence eligible for context after a fresh planning reset.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? HistoryStartSequence { get; init; }
    /// <summary>First transcript sequence shown to the user; set only when an expired conversation is reopened, so a reset hides old messages from the model but not from the chat.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TranscriptStartSequence { get; init; }
}

/// <summary>Where the traveler is on one day; that day's dishes are searched within the radius around this point.</summary>
/// <param name="District">The district around that point, shown to the user so a wrongly located place is noticed before any dish is chosen.</param>
public sealed record AgentDayArea(short DayNumber, string Label, decimal Latitude, decimal Longitude, decimal RadiusKm, string? District = null);

/// <summary>One meal whose search reached this radius around its named place, because nearer restaurants have nothing for it.</summary>
public sealed record AgentWidenedMeal(short DayNumber, string MealSlot, decimal RadiusKm);

/// <summary>Bounded user messages collected across clarification turns. The figures are no longer filled; they stay so conversations already stored can still be read.</summary>
public sealed record AgentPendingPlanningRequest(string[] Messages, short? NumDays = null,
    decimal? TotalBudget = null, decimal? DailyBudget = null);

/// <summary>The exact proposal version a save question is about, so a later "ok" can never save a different plan.</summary>
public sealed record AgentPendingSave(long ProposalId, int ProposalVersion, string Stage);

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
    AgentItem[]? RetainedItems = null, AgentDishNote[]? DishNotes = null, AgentDaySummary[]? DaySummaries = null, string[]? Notes = null);

/// <summary>Model-worded text about one dish in a preview; shown as the model wrote it. Never saved to the roadmap.</summary>
public sealed record AgentDishNote(int DishId, string? Intro, string? Reason);

/// <summary>Model-worded conclusion about one day of a preview, checked the same way.</summary>
public sealed record AgentDaySummary(short DayNumber, string Summary);

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
/// <param name="ScopeRejectedItemKeys">Set only when every error is a missing in-scope grant; internal repair input, never serialized.</param>
/// <param name="AvoidableRepeatItemKeys">New items repeating a dish name while an unused authorized dish fits that position; never blocking or serialized.</param>
public sealed record AgentValidation(string[] Errors, string[] Warnings, decimal TotalCost, AgentDayRoute[] Routes,
    [property: JsonIgnore] string[]? ScopeRejectedItemKeys = null,
    [property: JsonIgnore] string[]? AvoidableRepeatItemKeys = null);

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
    /// <summary>Set when the roadmap this conversation was attached to has since been deleted; the model is told so and the turn goes on without it.</summary>
    public bool AttachedRoadmapDeleted { get; init; }
    /// <summary>Set when the ready draft was built on a roadmap revision that has since been edited or deleted; it was left out and is discarded at the end of the turn.</summary>
    public bool OutdatedProposalDropped { get; init; }
    /// <summary>Meals outside the model's editMeals that its output changed and the backend put back from the source this turn.</summary>
    public AgentScope[] RestoredMeals { get; set; } = [];
    public required DTOs.Agent.SendAgentMessageRequestDto Request { get; init; }
    public List<AgentTranscript> History { get; init; } = [];
    /// <summary>Oldest history messages left out of the prompt when the turn would exceed the context limit; History itself stays whole for evidence checks.</summary>
    public int PromptHistorySkip { get; set; }
    /// <summary>The history the model reads this turn.</summary>
    public IEnumerable<AgentTranscript> PromptHistory => History.Skip(PromptHistorySkip);
    public List<AgentCandidateGrant> Grants { get; } = [];
    /// <summary>The rest of each ranked list, never sent to the model; only the backend uses it to replace an avoidable repeat.</summary>
    public List<AgentCandidateGrant> ReserveGrants { get; } = [];
    public List<AgentToolExchange> Exchanges { get; } = [];
    /// <summary>The model's wording for the preview this turn produced, attached to the final dishes.</summary>
    public AgentOutput? PreviewOutput { get; set; }
    /// <summary>Latest backend-observed availability per requested day/meal in this turn.</summary>
    public List<AgentMealCoverage> MealCoverage { get; } = [];
    public string? RepairFeedback { get; set; }
    /// <summary>Valid JSON proposal rejected by business validation, retained for a bounded model correction.</summary>
    public AgentOutput? RejectedOutput { get; set; }
    public bool ReplanAfterConfirmation { get; set; }
    public AgentState? ConstraintBaseline { get; set; }
    /// <summary>Dish/restaurant requests the backend placed on one meal this turn; a later model state cannot widen or drop them.</summary>
    public List<AgentMealRequest> PlacedRequests { get; } = [];
    public object[] CatalogTags { get; set; } = [];
    /// <summary>Catalog tag names by ID, used to check that a tag the model adds is grounded in the user's wording.</summary>
    public IReadOnlyDictionary<short, string> CatalogTagNames { get; set; } = new Dictionary<short, string>();
    /// <summary>Trusted tool failures/coverage hints used to construct the user-visible reply.</summary>
    public string? ToolNotice { get; set; }
    /// <summary>The last unmatched catalog lookup of the turn (name not found, near matches); kept when later rankings reset ToolNotice.</summary>
    public string? CatalogNotice { get; set; }
    /// <summary>The last exact catalog match of the turn; answers a direct catalog question but is never a clarification reply.</summary>
    public string? CatalogAnswer { get; set; }
    /// <summary>Set after a repeated identical tool request; the next provider call must return a final output.</summary>
    public bool ToolsClosed { get; set; }
    /// <summary>Set when a recommendation was requested before province, duration and budget were known; the turn asks for them.</summary>
    public bool TripDetailsMissing { get; set; }
    /// <summary>Set when the preferences question was asked before this message; once the model reads an answer in it, this turn plans.</summary>
    public bool IntakeComplete { get; set; }
    /// <summary>Provinces ANGI has restaurants in, loaded only while no plan exists; a province outside it never enters the trip.</summary>
    public IReadOnlyCollection<string>? CatalogProvinces { get; set; }
    /// <summary>A province the model read that ANGI has no data for; the turn says so and asks again.</summary>
    public string? UnknownPlace { get; set; }
    /// <summary>Set when the previous reply suggested a dish, so a short answer is read against that offer instead of as a bare acknowledgement.</summary>
    public bool DishOffered { get; set; }
    /// <summary>Days this message moved to another place on an existing plan; they need new dishes.</summary>
    public short[] MovedDays { get; set; } = [];
    public bool ItineraryChanged => MovedDays.Length > 0;
    /// <summary>How the backend read the place of this turn (a pinned point, shared coordinates, located names); told to the user once, above the next question.</summary>
    public string? PlaceNote { get; set; }
    /// <summary>What suggest_dish found; the turn ends once the model has worded it, because no plan is involved.</summary>
    public AgentDishSuggestion? DishSuggestion { get; set; }
    /// <summary>A long plan cut to the days this message names, sent to the model instead of Source; null sends Source whole.</summary>
    public AgentSnapshot? PromptSource { get; set; }
    /// <summary>The question the backend is waiting on, for the model to read this message against; null when nothing waits.</summary>
    public string? AwaitedAnswer { get; set; }
    /// <summary>What this provider call may do; the orchestrator moves it through the turn.</summary>
    public AgentTurnPhase Phase { get; set; } = AgentTurnPhase.Act;
    /// <summary>The tool named in the read step, for the forced call.</summary>
    public string? ForcedTool { get; set; }
}

/// <summary>Scoped evidence from actual recommendation results, never a model's claim of missing dishes.</summary>
public sealed record AgentMealCoverage(short DayNumber, string MealSlot, int AvailableCount,
    string Fingerprint, string[] RequiredTags);

/// <summary>One dish suggest_dish picked, with the only facts a reply may state about it.</summary>
/// <param name="Meals">The meals the dish fits, in words; null when it fits any meal or the user already named one.</param>
public sealed record AgentSuggestedDish(string Name, string Restaurant, decimal Price, string? Address, string? Meals = null);

/// <summary>The dishes picked for a suggestion, whether they were really ranked by the user's taste, and the fixed reply shown when the model's wording fails its check.</summary>
/// <param name="Area">The place the user named that the search stayed around.</param>
/// <param name="AreaNotFound">The place the user named that could not be located, so the search was not narrowed to it.</param>
public sealed record AgentDishSuggestion(AgentSuggestedDish[] Dishes, bool Personalized, bool AroundPin, string FixedReply, bool HasPlan = false, string? Area = null, string? AreaNotFound = null);

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
    /// <summary>Input tokens the provider read from its prompt cache, billed at a fraction of the normal rate; null when the provider did not report it.</summary>
    public int? CachedTokens { get; init; }

    /// <summary>Creates a failed step that cannot be mistaken for malformed model output requiring repair.</summary>
    public static AgentLlmStep Failed(AgentLlmFailureKind kind, int? httpStatus = null, int? retryAfterSeconds = null, int? tokens = null) =>
        new([], null, tokens, new(kind, httpStatus, retryAfterSeconds));
}

/// <summary>Allowlisted output returned by the LLM; prices and provenance are never accepted here.</summary>
public sealed record AgentOutput
{
    public required string Action { get; init; }
    public required string Reply { get; init; }
    /// <summary>Dish named in a general food question refused as out of scope; offered only if the user's own message names it.</summary>
    public string? SuggestedDish { get; init; }
    /// <summary>Allowlisted question intent; free provider text is never a factual response.</summary>
    public string? Clarification { get; init; }
    public AgentState? State { get; init; }
    public bool ConstraintsChangeConfirmed { get; init; }
    public int? BaseProposalVersion { get; init; }
    public AgentScope Scope { get; init; } = new();
    /// <summary>The days and meals the model read as the ones to change when editing an existing plan; the backend keeps every other meal from the source.</summary>
    public AgentScope[]? EditMeals { get; init; } = [];
    public AgentItem[] Items { get; init; } = [];
    public AgentChange[] Changes { get; init; } = [];
    public AgentDishNote[]? DishNotes { get; init; } = [];
    public AgentDaySummary[]? DaySummaries { get; init; } = [];
    /// <summary>The model's reading of a reply to the question the backend is waiting on; it grants nothing until AgentConfirmationPolicy has checked that question is open.</summary>
    public string? PendingAnswer { get; init; }
    /// <summary>The trip start the model read from a reply to the start date question, as yyyy-MM-dd; the backend still checks it.</summary>
    public string? AnswerStartDate { get; init; }
    /// <summary>The trip end when the reply named a range, as yyyy-MM-dd.</summary>
    public string? AnswerEndDate { get; init; }
    /// <summary>With action use_tool: the one tool the model asks for, because it has no tools while it reads a message.</summary>
    public string? Tool { get; init; }
}

/// <summary>What a provider call may do. A model holding tools tends to call one even when none fits, so it first reads the message with none.</summary>
public enum AgentTurnPhase
{
    /// <summary>All tools are offered: following up on data a tool returned, or repairing an output.</summary>
    Act,
    /// <summary>No tools: the model reads the message and answers it, or names the tool it needs.</summary>
    Read,
    /// <summary>Only the tool named in the read step, which the model must call.</summary>
    Forced
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
