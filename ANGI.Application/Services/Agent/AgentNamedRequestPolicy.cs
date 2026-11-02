using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>
/// Keeps "phải có món X" / "phải ăn ở quán Y" as one meal of the trip: a name scoped wider than a meal would otherwise force
/// that dish or restaurant on every meal and leave breakfast without candidates. Explicit "mọi bữa" wording keeps the wide scope.
/// </summary>
public static class AgentNamedRequestPolicy
{
    private const string EveryMeal = @"\b(?:ca chuyen|toan chuyen|toan bo|ca ngay|moi bua|moi ngay|hang ngay|tat ca|suot|chi an|chi o|chi co|bua nao cung|ngay nao cung)\b";
    // Ưu tiên bữa chính giữa ngày: món đặc trưng hợp bữa trưa/tối hơn bữa sáng.
    private static readonly string[] MealPreference = ["lunch", "dinner", "breakfast", "snack", "late_night"];

    /// <summary>The dish/restaurant requests wider than a meal whose wording does not ask for every meal.</summary>
    public static AgentMealRequest[] Placeable(AgentState state, IEnumerable<string> messages)
    {
        var texts = messages.Select(Normalize).ToArray();
        return state.MealRequests.Where(request => request.MealSlot is null && (request.DishName ?? request.RestaurantName) is { } name &&
            !texts.Any(text => text.Contains(Normalize(name), StringComparison.Ordinal) && Regex.IsMatch(text, EveryMeal, RegexOptions.CultureInvariant))).ToArray();
    }

    /// <summary>Meals to try for a placed request: declared meals only, or breakfast/lunch/dinner for legacy state.</summary>
    public static string[] Meals(AgentState state) => MealPreference
        .Where(meal => state.MealSlots.Length > 0 ? state.MealSlots.Contains(meal) : meal is "breakfast" or "lunch" or "dinner").ToArray();

    /// <summary>First day/meal in scope no other named request occupies; a whole-trip request starts from day 1.</summary>
    public static (short Day, string Meal)? Slot(AgentState state, AgentMealRequest request, IEnumerable<string> fitting)
    {
        var meals = fitting.ToArray();
        var days = request.DayNumber is { } only ? [only] : Enumerable.Range(1, state.NumDays ?? 0).Select(day => (short)day).ToArray();
        foreach (var day in days)
            foreach (var meal in meals)
                if (!state.MealRequests.Any(other => other.DayNumber == day && other.MealSlot == meal && (other.DishName ?? other.RestaurantName) is not null))
                    return (day, meal);
        return null;
    }

    /// <summary>
    /// Named meal requests the user did not place themselves: a backend placement may move when the name cannot serve that meal
    /// (a dinner-only restaurant replacing a lunch request), but "bữa trưa ngày 1 ăn ở X" stays and gets an honest question.
    /// </summary>
    public static AgentMealRequest[] Relocatable(AgentState state, IEnumerable<string> messages)
    {
        var named = messages.SelectMany(message => AgentMealScopePolicy.Read(message, state).Meals).ToHashSet();
        return state.MealRequests.Where(request => request.MealSlot is not null && (request.DishName ?? request.RestaurantName) is not null &&
            !named.Contains(new AgentScope(request.DayNumber, request.MealSlot))).ToArray();
    }

    /// <summary>Replaces a wide request with its placed meal; tags and differentFromPlan keep their wide scope without the name.</summary>
    public static AgentState Place(AgentState state, AgentMealRequest request, short day, string meal)
    {
        var requests = state.MealRequests.Where(other => !(other.DayNumber == request.DayNumber && other.MealSlot == request.MealSlot && SameNames(other, request))).ToList();
        var remainder = request with { DishName = null, RestaurantName = null, RestaurantId = null };
        if (remainder.TagIds.Length > 0 || remainder.DifferentFromPlan) requests.Add(remainder);
        requests.Add(request with { DayNumber = day, MealSlot = meal, TagIds = [], DifferentFromPlan = false });
        return state with { MealRequests = [.. requests] };
    }

    /// <summary>Moves the same request in the turn-start constraints, so the backend's own placement is not read as the user relaxing it.</summary>
    public static AgentState Rebase(AgentState baseline, AgentMealRequest request, short day, string meal) =>
        baseline.MealRequests.Any(other => other.DayNumber == request.DayNumber && other.MealSlot == request.MealSlot && SameNames(other, request))
            ? Place(baseline, request, day, meal) : baseline;

    /// <summary>Keeps this turn's placements when the model resends the wide request, or a state without the placed one.</summary>
    public static AgentState? Retain(AgentState? proposed, IReadOnlyCollection<AgentMealRequest> placed)
    {
        if (proposed is null || placed.Count == 0) return proposed;
        var requests = proposed.MealRequests.Where(request => !placed.Any(item => SameNames(item, request) &&
            (request.DayNumber != item.DayNumber || request.MealSlot != item.MealSlot))).ToList();
        requests.AddRange(placed.Where(item => !requests.Any(request => request.DayNumber == item.DayNumber && request.MealSlot == item.MealSlot)));
        return proposed with { MealRequests = [.. requests] };
    }

    private static bool SameNames(AgentMealRequest left, AgentMealRequest right) =>
        AgentScopedPlanning.Normalize(left.DishName) == AgentScopedPlanning.Normalize(right.DishName) &&
        AgentScopedPlanning.Normalize(left.RestaurantName) == AgentScopedPlanning.Normalize(right.RestaurantName);

    private static string Normalize(string text) => new string(text.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
        .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');
}
