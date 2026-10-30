using System.Security.Cryptography;
using System.Text;

namespace ANGI.Application.Common.Models.Agent;

/// <summary>Scoped taste targets; null meal means the whole day.</summary>
public sealed record AgentTasteOverride(short DayNumber, string? MealSlot, AgentTasteTarget[] Targets);

/// <summary>Catalog selectors for a trip, day or meal; not additional taste dimensions.</summary>
public sealed record AgentMealRequest(short? DayNumber, string? MealSlot, string? DishName,
    string? RestaurantName, short[] TagIds, bool DifferentFromPlan = false, long? RestaurantId = null);

/// <summary>Computes inheritance and provenance without mutating a profile or unrelated scope.</summary>
public static class AgentScopedPlanning
{
    /// <summary>Overlays only declared dimensions, from trip through day to meal.</summary>
    public static AgentTasteTarget[] Targets(AgentState state, short day, string meal) => state.TasteTargets
        .Concat(state.TasteOverrides.Where(x => x.DayNumber == day && x.MealSlot is null).SelectMany(x => x.Targets))
        .Concat(state.TasteOverrides.Where(x => x.DayNumber == day && x.MealSlot == meal).SelectMany(x => x.Targets))
        .GroupBy(x => x.Dimension).Select(x => x.Last()).OrderBy(x => x.Dimension, StringComparer.Ordinal).ToArray();

    /// <summary>Derives the request vector from the frozen profile without mutating it.</summary>
    public static double[]? Vector(AgentState state, short day, string meal) =>
        AgentTasteOntology.Apply(state.Preferences ?? new(), Targets(state, day, meal)).EffectiveVector;

    /// <summary>Combines catalog selectors independently of the ontology.</summary>
    public static AgentMealRequest Request(AgentState state, short day, string meal)
    {
        var applicable = state.MealRequests.Where(x => (!x.DayNumber.HasValue || x.DayNumber == day) &&
            (x.MealSlot is null || x.MealSlot == meal)).OrderBy(x => x.DayNumber.HasValue).ThenBy(x => x.MealSlot is not null).ToArray();
        return new(day, meal, applicable.LastOrDefault(x => x.DishName is not null)?.DishName,
            applicable.LastOrDefault(x => x.RestaurantName is not null)?.RestaurantName,
            applicable.SelectMany(x => x.TagIds).Distinct().Order().ToArray(), applicable.Any(x => x.DifferentFromPlan),
            applicable.LastOrDefault(x => x.RestaurantName is not null)?.RestaurantId);
    }

    /// <summary>Identifies effective ranking inputs without unrelated scope or timestamp metadata.</summary>
    public static string Fingerprint(AgentState state, short day, string meal)
    {
        var request = Request(state, day, meal) with { DayNumber = null, MealSlot = null };
        var json = AgentJson.Write(new { targets = Targets(state, day, meal), vector = Vector(state, day, meal),
            request = request with { DishName = Normalize(request.DishName), RestaurantName = Normalize(request.RestaurantName) } });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>Detects affected existing meals, preserving explicit future-only taste changes.</summary>
    public static bool RequiresRefresh(AgentState previous, AgentState current, AgentItem item) =>
        AgentJson.Write(Request(previous, item.DayNumber, item.MealSlot)) != AgentJson.Write(Request(current, item.DayNumber, item.MealSlot)) ||
        current.PreferenceApplication != "future" && (previous.PreferenceApplication == "future" ||
            AgentJson.Write(Targets(previous, item.DayNumber, item.MealSlot)) != AgentJson.Write(Targets(current, item.DayNumber, item.MealSlot)) ||
            previous.Preferences?.Initialized == true && Fingerprint(previous, item.DayNumber, item.MealSlot) != Fingerprint(current, item.DayNumber, item.MealSlot));

    /// <summary>Enumerates bounded day/meal positions to verify a ranking scope is homogeneous.</summary>
    public static IEnumerable<(short Day, string Meal)> Slots(AgentState state, AgentScope scope) =>
        Enumerable.Range(1, state.NumDays ?? 0).SelectMany(day => new[] { "breakfast", "lunch", "dinner", "snack", "late_night" }
            .Select(meal => ((short)day, meal))).Where(x => (!scope.DayNumber.HasValue || scope.DayNumber == x.Item1) &&
                (scope.MealSlot is null || scope.MealSlot == x.meal));

    /// <summary>Normalizes case and spacing without treating a near name as an exact identity.</summary>
    public static string? Normalize(string? name) => name is null ? null : string.Join(' ', name.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
