using System.Security.Cryptography;
using System.Text;
using ANGI.Application.Common.Models.Discovery;

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
        var targets = Targets(state, day, meal);
        var vector = Vector(state, day, meal);
        var named = request with { DishName = Normalize(request.DishName), RestaurantName = Normalize(request.RestaurantName) };
        // Ngày có nơi đến riêng được xếp hạng riêng. Ngày không có thì giữ nguyên chuỗi băm cũ để bản nháp đã lưu trước đây vẫn xác nhận được.
        var json = state.DayAreas?.FirstOrDefault(x => x.DayNumber == day) is { } place
            ? AgentJson.Write(new { targets, vector, request = named, area = new { place.Latitude, place.Longitude, place.RadiusKm } })
            : AgentJson.Write(new { targets, vector, request = named });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>The circle a day's dishes must fall in: the day's own place when the itinerary names one, otherwise the whole trip's origin and radius.</summary>
    public static NearbyArea? Area(AgentState state, short day) => state.DayAreas?.FirstOrDefault(x => x.DayNumber == day) is { } place
        ? new(place.Latitude, place.Longitude, place.RadiusKm)
        : state.RadiusKm is { } radius && state.OriginLatitude is { } latitude && state.OriginLongitude is { } longitude ? new(latitude, longitude, radius) : null;

    /// <summary>The circle one meal's dish must fall in: its day's circle, or the wider one when that meal had nothing nearer.</summary>
    public static NearbyArea? MealArea(AgentState state, short day, string meal) =>
        Area(state, day) is { } area && state.WidenedMeals?.FirstOrDefault(x => x.DayNumber == day && x.MealSlot == meal) is { } widened
            ? area with { RadiusKm = widened.RadiusKm } : Area(state, day);

    /// <summary>Records how far a meal's search reached on these days; a null radius means the meal found dishes without widening.</summary>
    public static AgentState WithWidenedMeal(AgentState state, IEnumerable<short> days, string meal, decimal? radius)
    {
        var scope = days.ToArray();
        var kept = (state.WidenedMeals ?? []).Where(x => x.MealSlot != meal || !scope.Contains(x.DayNumber));
        var meals = (radius is { } km ? kept.Concat(scope.Select(day => new AgentWidenedMeal(day, meal, km))) : kept).ToArray();
        return meals.Length == 0 && state.WidenedMeals is null ? state : state with { WidenedMeals = meals.Length > 0 ? meals : null };
    }

    /// <summary>
    /// The place the user named for this day, in words: a day of the itinerary or an area of the whole trip. Null for a whole-province day
    /// and for an origin from GPS or the map, whose search order stays as it was.
    /// </summary>
    public static NearbyArea? NamedArea(AgentState state, short day) => state.DayAreas?.FirstOrDefault(x => x.DayNumber == day) is { } place
        ? new(place.Latitude, place.Longitude, place.RadiusKm)
        : state.NamedAreaLabel is not null && state.NamedAreaLabel == state.OriginLabel ? Area(state, day) : null;

    /// <summary>Detects affected existing meals, preserving explicit future-only taste changes.</summary>
    public static bool RequiresRefresh(AgentState previous, AgentState current, AgentItem item) =>
        Area(previous, item.DayNumber) != Area(current, item.DayNumber) ||
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
