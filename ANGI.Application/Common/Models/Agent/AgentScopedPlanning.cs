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

    /// <summary>Past this many days the backend assembles a new plan itself: the model would have to write hundreds of items, more than one reply can hold.</summary>
    public const int LongTripDays = 14;

    /// <summary>Number of planned meals in the trip; legacy state without declared meals plans breakfast, lunch and dinner.</summary>
    public static int MealCount(AgentState state) => (state.NumDays ?? 0) * (state.MealSlots.Length > 0 ? state.MealSlots.Length : 3);

    /// <summary>The trip budget split evenly over its meals: a price reference for choosing dishes, never a hard per-meal cap.</summary>
    public static decimal? BudgetPerMeal(AgentState state) =>
        state.BudgetAmount is { } budget && MealCount(state) is > 0 and var meals ? decimal.Floor(budget / meals) : null;

    /// <summary>Adds the source dishes no longer in the plan to the replaced names; bounded so the context cost stays small.</summary>
    public static string[]? Replaced(AgentState state, AgentSnapshot? source, AgentSnapshot result)
    {
        var kept = result.Items.Select(item => Normalize(item.DishName)).ToHashSet();
        var dropped = (source?.Items ?? []).Where(item => item.DishName is not null && !kept.Contains(Normalize(item.DishName))).Select(item => item.DishName!);
        var names = (state.ReplacedDishNames ?? []).Where(name => !kept.Contains(Normalize(name))).Concat(dropped)
            .DistinctBy(Normalize).TakeLast(MaxReplacedNames).ToArray();
        return names.Length == 0 ? null : names;
    }

    private const int MaxReplacedNames = 40;

    /// <summary>Catalog category code of a main dish; drinks, desserts, starters and snacks are side items.</summary>
    public const string MainTagCode = "local_sf_main";

    /// <summary>
    /// Breakfast, lunch and dinner need a main dish by default. Any category tag, dish or restaurant the user asked for
    /// at that position (trip filters included) is an explicit choice that replaces the default, e.g. drinks-only breakfast.
    /// </summary>
    public static bool RequiresMain(AgentState state, short day, string meal, IEnumerable<Domain.Entities.Tag> catalogTags,
        IEnumerable<short>? searchTagIds = null)
    {
        if (meal is not ("breakfast" or "lunch" or "dinner")) return false;
        var request = Request(state, day, meal);
        if (request.DishName is not null || request.RestaurantName is not null) return false;
        var categories = catalogTags.Where(tag => tag.TagType == Domain.Enums.TagType.Category).Select(tag => tag.Id).ToHashSet();
        return !state.TagIds.Concat(request.TagIds).Concat(searchTagIds ?? []).Any(categories.Contains);
    }

    /// <summary>Normalizes case and spacing without treating a near name as an exact identity.</summary>
    public static string? Normalize(string? name) => name is null ? null : string.Join(' ', name.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
