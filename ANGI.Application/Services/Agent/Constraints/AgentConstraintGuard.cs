using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Intake;
using ANGI.Application.Services.Agent.Places;
using ANGI.Application.UseCases.Agent;
using ANGI.Application.UseCases.Validators.Agent;
using FluentValidation;

namespace ANGI.Application.Services.Agent.Constraints;

/// <summary>
/// Guards what the user asked for against what the model proposes: it accepts state enrichment but not a relaxed constraint, requires the user's own words for a taste level or a budget,
/// compares constraints, records meals a patch empties and keeps a named dish or restaurant on one meal.
/// </summary>
public sealed class AgentConstraintGuard
{
    // Chỉ khi người dùng tự nhắc tới điểm chấm/bán kính thì model mới được bỏ chúng.
    private static readonly Regex NamesOrigin = new(@"\b(?:ban kinh|diem xuat phat|diem (?:da )?cham|ban do|vi tri|ghim|toan (?:thanh pho|tinh|tp)|ca (?:thanh pho|tinh|tp))\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// A trip value the model left empty is not a request to erase it: conversation 138 turned "chỉ gợi ý một món" into a state with no duration, budget or pin,
    /// and a following "ok" wiped them. Removing a budget limit goes through budgetUnrestricted; a pin goes only when the user names it.
    /// </summary>
    public static AgentState? WithoutErased(AgentState? proposed, AgentState current, string content)
    {
        // STEP-01: Return null early when proposed is missing.
        if (proposed is null) return null;
        // STEP-02: Prepare plain through Normalize().ToLowerInvariant().Replace, keepsOrigin from NamesOrigin.IsMatch and origin.
        var plain = new string(content.Normalize(NormalizationForm.FormD).Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');
        var keepsOrigin = !NamesOrigin.IsMatch(plain);
        var origin = keepsOrigin && proposed.OriginMode == "none" && current.OriginMode != "none"
            ? proposed with { OriginMode = current.OriginMode, OriginLatitude = current.OriginLatitude, OriginLongitude = current.OriginLongitude, OriginLabel = current.OriginLabel } : proposed;
        // STEP-03: Return a copy of origin with updated fields.
        return origin with
        {
            Province = string.IsNullOrWhiteSpace(proposed.Province) ? current.Province : proposed.Province,
            NumDays = proposed.NumDays ?? current.NumDays,
            BudgetAmount = proposed.BudgetAmount ?? (proposed.BudgetUnrestricted ? null : current.BudgetAmount),
            RadiusKm = proposed.RadiusKm ?? (keepsOrigin ? current.RadiusKm : null)
        };
    }

    /// <summary>Requires a backend-staged human confirmation before relaxing an existing trip requirement.</summary>
    public AgentState Accept(AgentState current, AgentState? proposed, AgentState? baseline = null)
    {
        // STEP-01: Return current early when proposed is missing and current.PendingPlanningRequest is missing.
        if (proposed is null && current.PendingPlanningRequest is null) return current;
        // STEP-02: Set proposed, without the quotes the model cited for its taste levels and budget.
        proposed = WithoutQuotes(proposed ?? current);
        if (proposed.BudgetQuote is not null) proposed = proposed with { BudgetQuote = null };
        // STEP-03: When proposed.PendingPlanningRequest is not current.PendingPlanningRequest and more.
        if (proposed.PendingPlanningRequest != current.PendingPlanningRequest || proposed.PendingSave != current.PendingSave || proposed.PlanningIntake != current.PlanningIntake || proposed.DishOffered != current.DishOffered)
            proposed = proposed with { PendingPlanningRequest = current.PendingPlanningRequest, PendingSave = current.PendingSave, PlanningIntake = current.PlanningIntake, DishOffered = current.DishOffered };
        // STEP-04: When proposed.FocusDay is not current.FocusDay or proposed.FocusMeal is not current.FocusMeal, set proposed.
        // Taste scopes belong to overrides/requests, not navigation focus. A model cannot invent or clear a UI pin.
        if (proposed.FocusDay != current.FocusDay || proposed.FocusMeal != current.FocusMeal)
            proposed = proposed with { FocusDay = current.FocusDay, FocusMeal = current.FocusMeal };
        // STEP-06: When proposed.Preferences is not current.Preferences, set proposed.
        // Provider and client state can only change targets; trusted vectors always come from reco.
        if (proposed.Preferences != current.Preferences) proposed = proposed with { Preferences = current.Preferences };
        // STEP-07: When ReferenceEquals(proposed.ReplacedDishNames, current.ReplacedDishNames) does not hold or ReferenceEquals(proposed.SkippedMeals and more.
        if (!ReferenceEquals(proposed.ReplacedDishNames, current.ReplacedDishNames) || !ReferenceEquals(proposed.SkippedMeals, current.SkippedMeals))
            proposed = proposed with { ReplacedDishNames = current.ReplacedDishNames, SkippedMeals = current.SkippedMeals };
        // STEP-08: Prepare movedProvince from Trim, numDays and areas from current.DayAreas ?? [].Where.
        // Nơi đến theo ngày do backend đọc từ lời người dùng, model không được ghi. Đổi tỉnh hoặc rút ngắn chuyến thì bỏ các nơi đến không còn áp dụng.
        var movedProvince = current.Province is not null && !string.Equals(current.Province.Trim(), proposed.Province?.Trim(), StringComparison.OrdinalIgnoreCase);
        var numDays = proposed.NumDays;
        var areas = movedProvince ? [] : (current.DayAreas ?? []).Where(x => !numDays.HasValue || x.DayNumber <= numDays).ToArray();
        // STEP-09: When areas.SequenceEqual(...) does not hold, set proposed.
        if (!areas.SequenceEqual(proposed.DayAreas ?? [])) proposed = proposed with { DayAreas = areas.Length > 0 ? areas : null };
        // STEP-10: Prepare widened from current.WidenedMeals ?? [].Where.
        var widened = movedProvince ? [] : (current.WidenedMeals ?? []).Where(x => !numDays.HasValue || x.DayNumber <= numDays).ToArray();
        // STEP-11: When widened.SequenceEqual(...) does not hold, set proposed.
        if (!widened.SequenceEqual(proposed.WidenedMeals ?? [])) proposed = proposed with { WidenedMeals = widened.Length > 0 ? widened : null };
        // STEP-12: When proposed.PendingDayArea is not current.PendingDayArea or proposed.NamedAreaLabel is not current.NamedAreaLabel, set proposed.
        if (proposed.PendingDayArea != current.PendingDayArea || proposed.NamedAreaLabel != current.NamedAreaLabel)
            proposed = proposed with { PendingDayArea = current.PendingDayArea, NamedAreaLabel = current.NamedAreaLabel };
        // STEP-13: Validate proposed.
        new AgentStateValidator().ValidateAndThrow(proposed);
        // STEP-14: When proposed.Preferences.Initialized is true, set proposed.
        if (proposed.Preferences?.Initialized == true)
            proposed = proposed with { Preferences = AgentTasteOntology.Apply(proposed.Preferences, proposed.TasteTargets) };
        // STEP-15: Prepare confirmed and relaxed from AgentScopedPlanning.Slots.
        var confirmed = baseline ?? current;
        var relaxed = confirmed.Province is not null && !string.Equals(confirmed.Province.Trim(), proposed.Province?.Trim(), StringComparison.OrdinalIgnoreCase)
            || confirmed.NumDays.HasValue && proposed.NumDays != confirmed.NumDays
            || confirmed.BudgetAmount.HasValue && (!proposed.BudgetAmount.HasValue || proposed.BudgetAmount > confirmed.BudgetAmount)
            || !confirmed.BudgetUnrestricted && proposed.BudgetUnrestricted
            || confirmed.MaxPricePerMeal.HasValue && (!proposed.MaxPricePerMeal.HasValue || proposed.MaxPricePerMeal > confirmed.MaxPricePerMeal)
            || confirmed.ExcludeTagIds.Except(proposed.ExcludeTagIds).Any()
            || confirmed.TagIds.Except(proposed.TagIds).Any()
            || AgentScopedPlanning.Slots(confirmed, new()).Any(slot =>
            {
                var before = AgentScopedPlanning.Request(confirmed, slot.Day, slot.Meal);
                var after = AgentScopedPlanning.Request(proposed, slot.Day, slot.Meal);
                return before.TagIds.Except(after.TagIds).Any() || before.DifferentFromPlan && !after.DifferentFromPlan ||
                    before.DishName is not null && AgentScopedPlanning.Normalize(before.DishName) != AgentScopedPlanning.Normalize(after.DishName) ||
                    before.RestaurantName is not null && (AgentScopedPlanning.Normalize(before.RestaurantName) != AgentScopedPlanning.Normalize(after.RestaurantName) ||
                        before.RestaurantId.HasValue && before.RestaurantId != after.RestaurantId);
            })
            || confirmed.RadiusKm.HasValue && (!proposed.RadiusKm.HasValue || proposed.RadiusKm > confirmed.RadiusKm)
            || confirmed.OriginMode != "none" && (proposed.OriginMode != confirmed.OriginMode || proposed.OriginLatitude != confirmed.OriginLatitude || proposed.OriginLongitude != confirmed.OriginLongitude);
        // STEP-16: Throw AgentStateChangeRequiredException when relaxed is true.
        if (relaxed) throw new AgentStateChangeRequiredException(proposed);
        // STEP-17: Return proposed early when proposed.PendingStateChange is current.PendingStateChange and more.
        if (proposed.PendingStateChange == current.PendingStateChange && proposed.SourceRoadmapUpdatedAt == current.SourceRoadmapUpdatedAt &&
            proposed.HistoryStartSequence == current.HistoryStartSequence) return proposed;
        // STEP-18: Return a copy of proposed with updated fields.
        return proposed with { PendingStateChange = current.PendingStateChange, SourceRoadmapUpdatedAt = current.SourceRoadmapUpdatedAt,
            HistoryStartSequence = current.HistoryStartSequence };
    }

    // Taste evidence

    // Các từ này chỉ nói về sở thích, không nêu mức: "thích ăn cay" chưa cho biết cay bao nhiêu.
    private static readonly HashSet<string> NoLevelWords = ["thich", "an", "mon", "vi", "do", "khau", "muon", "uu", "tien", "toi", "minh", "em", "la", "va", "ma"];

    /// <summary>The dimensions the proposed state sets or changes with no quoted level; empty when the state is backed or the user already confirmed it.</summary>
    public static string[] UnbackedTastes(AgentContext context, AgentState? proposed)
    {
        // STEP-01: Return an empty list early when proposed is missing or context.ReplanAfterConfirmation is true.
        if (proposed is null || context.ReplanAfterConfirmation) return [];
        // STEP-02: Prepare written from the user's messages.
        var written = Plain(string.Join(' ', context.History.Where(turn => turn.Role == "user").Select(turn => turn.Content)
            .Concat(context.State.PendingPlanningRequest?.Messages ?? []).Append(context.Request.Content)));
        // STEP-03: Prepare known targets of the current state and return the proposed ones that are new and unbacked.
        var known = Scoped(context.State).Select(entry => (entry.Day, entry.Meal, entry.Target.Dimension, entry.Target.Target)).ToHashSet();
        return Scoped(proposed).Where(entry => !known.Contains((entry.Day, entry.Meal, entry.Target.Dimension, entry.Target.Target)) && !Backed(entry.Target, written))
            .Select(entry => entry.Target.Dimension).Distinct().ToArray();
    }

    /// <summary>Drops the quotes once checked, so they never reach stored state or a comparison of constraints.</summary>
    public static AgentState WithoutQuotes(AgentState state) => Scoped(state).All(entry => entry.Target.LevelQuote is null) ? state : state with
    {
        TasteTargets = state.TasteTargets.Select(target => target with { LevelQuote = null }).ToArray(),
        TasteOverrides = state.TasteOverrides.Select(entry => entry with { Targets = entry.Targets.Select(target => target with { LevelQuote = null }).ToArray() }).ToArray()
    };

    // Mỗi mục tiêu kèm phạm vi của nó: cùng một vị nhưng khác ngày là một yêu cầu mới.
    private static IEnumerable<(short? Day, string? Meal, AgentTasteTarget Target)> Scoped(AgentState state) =>
        state.TasteTargets.Select(target => ((short?)null, (string?)null, target))
            .Concat(state.TasteOverrides.SelectMany(entry => entry.Targets.Select(target => ((short?)entry.DayNumber, entry.MealSlot, target))));

    private static bool Backed(AgentTasteTarget target, string written)
    {
        // STEP-01: Prepare quote through Plain and return false early when it is empty or the user never wrote it.
        var quote = Plain(target.LevelQuote ?? "");
        if (quote.Length == 0 || !written.Contains(quote, StringComparison.Ordinal)) return false;
        // STEP-02: Prepare tastes and return whether a word remains that is neither the taste nor a word of liking.
        var tastes = AgentTasteClarificationPolicy.MentionedTastes(target.LevelQuote!).SelectMany(taste => Plain(taste).Split(' ')).ToHashSet();
        return quote.Split(' ').Any(word => !tastes.Contains(word) && !NoLevelWords.Contains(word));
    }

    private static string Plain(string text)
    {
        var plain = new string(text.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');
        return Regex.Replace(Regex.Replace(plain, @"[^a-z0-9 ]+", " "), @"\s+", " ").Trim();
    }

    // Budget evidence

    // Chỉ đọc số viết kèm đơn vị quen thuộc hoặc số đủ lớn để tự nó là tiền. Cách nói khác ("2 củ", "năm trăm") không bị bác: khi đó chỉ kiểm tra câu trích có thật.
    private static readonly Regex Amount = new(@"(?<amount>\d+(?:[.,]\d+)*)\s*(?<unit>trieu|tr|nghin|ngan|k)?\b", RegexOptions.CultureInvariant);
    private const decimal SmallestBareAmount = 1000m;
    // Cho phép chia theo người, nhân theo ngày và phần lẻ chưa đọc được ("1 triệu rưỡi"); chỉ bắt chênh lệch cỡ đơn vị.
    private const decimal Tolerance = 10m;

    /// <summary>True when the proposed state sets a budget with no quote of the user's words, or one whose size does not fit the figure quoted.</summary>
    public static bool HasUnbackedBudget(AgentContext context, AgentState? proposed)
    {
        // STEP-01: Return false early when no new budget is proposed or the user already confirmed it.
        if (proposed?.BudgetAmount is not { } budget || budget == context.State.BudgetAmount || context.ReplanAfterConfirmation) return false;
        // STEP-02: Prepare quote and written, then return true early when the user never wrote the quote.
        var quote = AgentIntakePolicy.Plain(proposed.BudgetQuote ?? "");
        var written = AgentIntakePolicy.Plain(string.Join(' ', context.History.Where(turn => turn.Role == "user").Select(turn => turn.Content)
            .Concat(context.State.PendingPlanningRequest?.Messages ?? []).Append(context.Request.Content)));
        if (quote.Length == 0 || !$" {written} ".Contains($" {quote} ", StringComparison.Ordinal)) return true;
        // STEP-03: Return whether every figure quoted is of a different size from the budget.
        var figures = Figures(proposed.BudgetQuote!).ToArray();
        var days = Math.Max((decimal)(proposed.NumDays ?? 1), 1m);
        return figures.Length > 0 && !figures.Any(figure => budget >= figure / Tolerance && budget <= figure * days * Tolerance);
    }

    /// <summary>The amounts in the quote that are clearly money: with a known unit, or large enough alone.</summary>
    private static IEnumerable<decimal> Figures(string quote)
    {
        // STEP-01: Go through each number; the separators of a number with no unit only group its digits, while with a unit a comma is a decimal point.
        foreach (Match match in Amount.Matches(AgentPlaceResolver.Fold(quote.Normalize(NormalizationForm.FormC))))
        {
            var unit = match.Groups["unit"].Value;
            var digits = unit.Length == 0 ? match.Groups["amount"].Value.Replace(".", "").Replace(",", "") : match.Groups["amount"].Value.Replace(',', '.');
            if (!decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) continue;
            var amount = value * (unit is "trieu" or "tr" ? 1000000m : unit.Length > 0 ? 1000m : 1m);
            // STEP-02: Yield the amount when its unit or size says it is money.
            if (unit.Length > 0 || amount >= SmallestBareAmount) yield return amount;
        }
    }

    // Comparing constraints

    /// <summary>
    /// Removes the dish/restaurant names the turn added to meal requests, keeping their tags; used when the turn ends on a catalog
    /// question, because a name the catalog did not settle is not a user constraint and picking another must need no confirmation.
    /// </summary>
    public static AgentState WithoutUnsettledNames(AgentState state, AgentState baseline)
    {
        // STEP-01: Prepare requests as List<AgentMealRequest>.
        var requests = new List<AgentMealRequest>();
        // STEP-02: For each request in state.MealRequests, skip this round when (request.DishName is missing and more.
        foreach (var request in state.MealRequests)
        {
            if (request.DishName is null && request.RestaurantName is null || baseline.MealRequests.Any(previous => SameRequestNames(previous, request)))
            { requests.Add(request); continue; }
            var unnamed = request with { DishName = null, RestaurantName = null, RestaurantId = null };
            if (unnamed.TagIds.Length > 0 || unnamed.DifferentFromPlan) requests.Add(unnamed);
        }
        // STEP-03: Return a copy of state with updated fields.
        return state with { MealRequests = [.. requests] };
    }

    private static bool SameRequestNames(AgentMealRequest previous, AgentMealRequest current) => previous.DayNumber == current.DayNumber &&
        previous.MealSlot == current.MealSlot && previous.DishName == current.DishName && previous.RestaurantName == current.RestaurantName &&
        previous.RestaurantId == current.RestaurantId;

    // Removed meals

    /// <summary>Adds the meals the remaining items no longer cover to the skipped meals, dropping meals that have dishes again.</summary>
    public static AgentState WithSkippedMeals(AgentState state, AgentSnapshot source, IReadOnlyCollection<AgentItem> remaining)
    {
        var emptied = source.Items.Select(item => new AgentScope(item.DayNumber, item.MealSlot))
            .Where(slot => !remaining.Any(item => item.DayNumber == slot.DayNumber && item.MealSlot == slot.MealSlot));
        return state with { SkippedMeals = Uncovered((state.SkippedMeals ?? []).Concat(emptied).Distinct(), remaining) };
    }

    /// <summary>The skipped meals that still have no dish; null when none remain, so state stays free of empty backend fields.</summary>
    public static AgentScope[]? Uncovered(IEnumerable<AgentScope> meals, IReadOnlyCollection<AgentItem> items)
    {
        var empty = meals.Where(slot => !items.Any(item => item.DayNumber == slot.DayNumber && item.MealSlot == slot.MealSlot)).ToArray();
        return empty.Length == 0 ? null : empty;
    }

    // Named dishes and restaurants

    private const string EveryMeal = @"\b(?:ca chuyen|toan chuyen|toan bo|ca ngay|moi bua|moi ngay|hang ngay|tat ca|suot|chi an|chi o|chi co|bua nao cung|ngay nao cung)\b";
    // Ưu tiên bữa chính giữa ngày: món đặc trưng hợp bữa trưa/tối hơn bữa sáng.
    private static readonly string[] MealPreference = ["lunch", "dinner", "breakfast", "snack", "late_night"];

    /// <summary>The dish/restaurant requests wider than a meal whose wording does not ask for every meal.</summary>
    public static AgentMealRequest[] PlaceableRequests(AgentState state, IEnumerable<string> messages)
    {
        var texts = messages.Select(Normalize).ToArray();
        return state.MealRequests.Where(request => request.MealSlot is null && (request.DishName ?? request.RestaurantName) is { } name &&
            !texts.Any(text => text.Contains(Normalize(name), StringComparison.Ordinal) && Regex.IsMatch(text, EveryMeal, RegexOptions.CultureInvariant))).ToArray();
    }

    /// <summary>Meals to try for a placed request: declared meals only, or breakfast/lunch/dinner for legacy state.</summary>
    public static string[] PlaceableMeals(AgentState state) => MealPreference
        .Where(meal => state.MealSlots.Length > 0 ? state.MealSlots.Contains(meal) : meal is "breakfast" or "lunch" or "dinner").ToArray();

    /// <summary>First day/meal in scope no other named request occupies; a whole-trip request starts from day 1.</summary>
    public static (short Day, string Meal)? PlaceSlot(AgentState state, AgentMealRequest request, IEnumerable<string> fitting)
    {
        // STEP-01: Prepare meals through fitting.ToArray and days from Enumerable.Range.
        var meals = fitting.ToArray();
        var days = request.DayNumber is { } only ? [only] : Enumerable.Range(1, state.NumDays ?? 0).Select(day => (short)day).ToArray();
        // STEP-02: For each day in days, go through each meal in meals.
        foreach (var day in days)
            foreach (var meal in meals)
                if (!state.MealRequests.Any(other => other.DayNumber == day && other.MealSlot == meal && (other.DishName ?? other.RestaurantName) is not null))
                    return (day, meal);
        // STEP-03: Return null.
        return null;
    }

    /// <summary>
    /// Named meal requests the user did not place themselves: a backend placement may move when the name cannot serve that meal
    /// (a dinner-only restaurant replacing a lunch request), but "bữa trưa ngày 1 ăn ở X" stays and gets an honest question.
    /// </summary>
    public static AgentMealRequest[] RelocatableRequests(AgentState state, IEnumerable<string> messages)
    {
        var named = messages.SelectMany(message => AgentMealScopePolicy.Read(message, state).Meals).ToHashSet();
        return state.MealRequests.Where(request => request.MealSlot is not null && (request.DishName ?? request.RestaurantName) is not null &&
            !named.Contains(new AgentScope(request.DayNumber, request.MealSlot))).ToArray();
    }

    /// <summary>Replaces a wide request with its placed meal; tags and differentFromPlan keep their wide scope without the name.</summary>
    public static AgentState PlaceRequest(AgentState state, AgentMealRequest request, short day, string meal)
    {
        // STEP-01: Prepare requests through state.MealRequests.Where(filtered).ToList and remainder.
        var requests = state.MealRequests.Where(other => !(other.DayNumber == request.DayNumber && other.MealSlot == request.MealSlot && SameNames(other, request))).ToList();
        var remainder = request with { DishName = null, RestaurantName = null, RestaurantId = null };
        // STEP-02: When remainder.TagIds.Length > 0 or remainder.DifferentFromPlan is true, call requests.Add.
        if (remainder.TagIds.Length > 0 || remainder.DifferentFromPlan) requests.Add(remainder);
        // STEP-03: Add a copy of request to requests.
        requests.Add(request with { DayNumber = day, MealSlot = meal, TagIds = [], DifferentFromPlan = false });
        // STEP-04: Return a copy of state with updated fields.
        return state with { MealRequests = [.. requests] };
    }

    /// <summary>Moves the same request in the turn-start constraints, so the backend's own placement is not read as the user relaxing it.</summary>
    public static AgentState RebasePlacement(AgentState baseline, AgentMealRequest request, short day, string meal) =>
        baseline.MealRequests.Any(other => other.DayNumber == request.DayNumber && other.MealSlot == request.MealSlot && SameNames(other, request))
            ? PlaceRequest(baseline, request, day, meal) : baseline;

    /// <summary>Keeps this turn's placements when the model resends the wide request, or a state without the placed one.</summary>
    public static AgentState? RetainPlacements(AgentState? proposed, IReadOnlyCollection<AgentMealRequest> placed)
    {
        // STEP-01: Return proposed early when proposed is missing or placed.Count is 0.
        if (proposed is null || placed.Count == 0) return proposed;
        // STEP-02: Prepare requests through MealRequests.Where(filtered).ToList.
        var requests = proposed.MealRequests.Where(request => !placed.Any(item => SameNames(item, request) &&
            (request.DayNumber != item.DayNumber || request.MealSlot != item.MealSlot))).ToList();
        // STEP-03: Add the built entries to requests.
        requests.AddRange(placed.Where(item => !requests.Any(request => request.DayNumber == item.DayNumber && request.MealSlot == item.MealSlot)));
        // STEP-04: Return a copy of proposed with updated fields.
        return proposed with { MealRequests = [.. requests] };
    }

    private static bool SameNames(AgentMealRequest left, AgentMealRequest right) =>
        AgentScopedPlanning.Normalize(left.DishName) == AgentScopedPlanning.Normalize(right.DishName) &&
        AgentScopedPlanning.Normalize(left.RestaurantName) == AgentScopedPlanning.Normalize(right.RestaurantName);

    private static string Normalize(string text) => new string(text.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
        .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');
}
