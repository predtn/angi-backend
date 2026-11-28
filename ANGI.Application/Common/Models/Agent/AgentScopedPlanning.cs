using System.Security.Cryptography;
using System.Text;
using ANGI.Application.Common.Models.Discovery;

namespace ANGI.Application.Common.Models.Agent;

/// <summary>Scoped taste targets; null meal means the whole day.</summary>
public sealed record AgentTasteOverride(short DayNumber, string? MealSlot, AgentTasteTarget[] Targets);

/// <summary>Catalog selectors for a trip, day or meal; not additional taste dimensions.</summary>
/// <param name="DishId">
/// The exact dish of a suggestion the user picked from offeredDishes, so the plan gets that dish at that restaurant rather than another one of the same name;
/// the backend accepts only an id the chat's suggestions showed. Left out of the JSON when null, so fingerprints of earlier drafts stay the same.
/// </param>
public sealed record AgentMealRequest(short? DayNumber, string? MealSlot, string? DishName,
    string? RestaurantName, short[] TagIds, bool DifferentFromPlan = false, long? RestaurantId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? DishId = null);

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
        // A picked dish's id belongs to the request that names it, so it never joins another request's name.
        var named = applicable.LastOrDefault(x => x.DishName is not null);
        return new(day, meal, named?.DishName,
            applicable.LastOrDefault(x => x.RestaurantName is not null)?.RestaurantName,
            applicable.SelectMany(x => x.TagIds).Distinct().Order().ToArray(), applicable.Any(x => x.DifferentFromPlan),
            applicable.LastOrDefault(x => x.RestaurantName is not null)?.RestaurantId, named?.DishId);
    }

    /// <summary>Identifies effective ranking inputs without unrelated scope or timestamp metadata.</summary>
    public static string Fingerprint(AgentState state, short day, string meal)
    {
        var request = Request(state, day, meal) with { DayNumber = null, MealSlot = null };
        var targets = Targets(state, day, meal);
        var vector = Vector(state, day, meal);
        var named = request with { DishName = Normalize(request.DishName), RestaurantName = Normalize(request.RestaurantName) };
        // Ngày có nơi đến riêng được xếp hạng riêng. Ngày không có thì giữ nguyên chuỗi băm cũ để bản nháp đã lưu trước đây vẫn xác nhận được.
        // Vùng nhiều nơi thêm các vòng tròn vào chuỗi băm; ngày một nơi giữ nguyên chuỗi cũ.
        var json = state.DayAreas?.FirstOrDefault(x => x.DayNumber == day) is { } place
            ? place.Zone is { Length: > 0 } zone
                ? AgentJson.Write(new { targets, vector, request = named, area = new { place.Latitude, place.Longitude, place.RadiusKm, zone } })
                : AgentJson.Write(new { targets, vector, request = named, area = new { place.Latitude, place.Longitude, place.RadiusKm } })
            : AgentJson.Write(new { targets, vector, request = named });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>The circle a day's dishes must fall in: the day's own place when the itinerary names one, otherwise the whole trip's origin and radius.</summary>
    /// <remarks>A zone read as several places is every one of its circles.</remarks>
    public static NearbyArea? Area(AgentState state, short day) => state.DayAreas?.FirstOrDefault(x => x.DayNumber == day) is { } place
        ? Circles(place.Zone, place.Latitude, place.Longitude, place.RadiusKm)
        : state.RadiusKm is { } radius && state.OriginLatitude is { } latitude && state.OriginLongitude is { } longitude
            ? Circles(state.OriginMode == "pinned" ? state.OriginZone : null, latitude, longitude, radius) : null;

    /// <summary>
    /// The circle of a point, with the zone's other circles when the zone still starts at this point; a zone left behind by a later origin (a point on
    /// the map, GPS) is ignored. Each place keeps its own radius until the radius changes, which then applies to every place of the zone.
    /// </summary>
    public static NearbyArea Circles(AgentZoneCircle[]? zone, decimal latitude, decimal longitude, decimal radiusKm)
    {
        if (zone is not { Length: > 1 } || zone[0].Latitude != latitude || zone[0].Longitude != longitude) return new(latitude, longitude, radiusKm);
        var area = new NearbyArea(latitude, longitude, zone[0].RadiusKm) { Also = zone[1..].Select(circle => new NearbyArea(circle.Latitude, circle.Longitude, circle.RadiusKm)).ToArray() };
        return radiusKm == zone[0].RadiusKm ? area : area.WithRadius(radiusKm);
    }

    /// <summary>True when the whole trip searches a zone of several places rather than around one point.</summary>
    public static bool HasZoneOrigin(AgentState state) => state.OriginMode == "pinned" && state.RadiusKm is { } radius && state.OriginLatitude is { } latitude &&
        state.OriginLongitude is { } longitude && Circles(state.OriginZone, latitude, longitude, radius).Also is { Length: > 0 };

    /// <summary>
    /// Where a day starts: the place the user named for that day, otherwise their current location or the point they pinned on the map; null when the
    /// trip searches the whole province, and null for a zone, whose first place is no more the start than any other, so its meals are not pulled there.
    /// </summary>
    public static (double Latitude, double Longitude)? StartPoint(AgentState state, short day) =>
        state.DayAreas?.FirstOrDefault(area => area.DayNumber == day) is { } place ? place.Zone is { Length: > 1 } ? null : ((double)place.Latitude, (double)place.Longitude)
        : HasZoneOrigin(state) ? null
        : state.OriginMode is "gps" or "pinned" && state.OriginLatitude is { } latitude && state.OriginLongitude is { } longitude ? ((double)latitude, (double)longitude)
        : null;

    /// <summary>The circle one meal's dish must fall in: its day's circle, or the wider one when that meal had nothing nearer.</summary>
    public static NearbyArea? MealArea(AgentState state, short day, string meal) =>
        Area(state, day) is { } area && state.WidenedMeals?.FirstOrDefault(x => x.DayNumber == day && x.MealSlot == meal) is { } widened
            ? area.WithRadius(widened.RadiusKm) : Area(state, day);

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
        ? Circles(place.Zone, place.Latitude, place.Longitude, place.RadiusKm)
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

    public static bool IsLongTrip(AgentState state) => state.NumDays > LongTripDays;

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

    // Kinds of dish that are a meal themselves: asking for one still means its main dishes, so "Cơm &amp; xôi" for lunch is not a side of plain rice.
    private static readonly HashSet<string> MainKindCodes = ["form.mon_com_va_ngu_coc", "form.mon_soi", "form.banh_mi_va_banh_kep", "form.lau", "form.chao",
        "form.banh_bot_va_mon_nhan", "form.muc_menu_nhieu_cau_phan", "form.khac_cho_duyet"];
    private static readonly HashSet<string> MainRoleCodes = ["role.mon_chinh", "role.bua_du_trong_mot_mon"];

    /// <summary>
    /// True when asking for this tag means something other than a main dish: a side kind such as drinks, or a meal role such as starter.
    /// No such dish carries the main-dish tag on the catalog, so requiring both would find nothing.
    /// </summary>
    public static bool ReplacesMain(Domain.Entities.Tag tag) => tag.TagType switch
    {
        Domain.Enums.TagType.Category => !MainKindCodes.Contains(tag.Code),
        Domain.Enums.TagType.MealRole => !MainRoleCodes.Contains(tag.Code),
        _ => false
    };

    /// <summary>
    /// Splits required tags into those a dish must all have and groups it needs one of. Several category, meal role, cooking method or ingredient
    /// tags mean any of them ("cơm hoặc bún": a dish has one kind). The main-dish tag, diets, meals and unknown ids always stay required.
    /// </summary>
    public static (short[] All, short[][] AnyOf) SplitTags(IEnumerable<short> tagIds, IEnumerable<Domain.Entities.Tag> catalogTags)
    {
        var catalog = catalogTags.GroupBy(tag => tag.Id).ToDictionary(group => group.Key, group => group.First());
        var ids = tagIds.Distinct().ToArray();
        bool Alternative(short id) => catalog.TryGetValue(id, out var tag) && tag.Code != MainTagCode && tag.TagType is Domain.Enums.TagType.Category
            or Domain.Enums.TagType.MealRole or Domain.Enums.TagType.CookingMethod or Domain.Enums.TagType.Ingredient;
        var groups = ids.Where(Alternative).GroupBy(id => catalog[id].TagType).Where(group => group.Count() > 1).Select(group => group.ToArray()).ToArray();
        return (ids.Except(groups.SelectMany(group => group)).ToArray(), groups);
    }

    /// <summary>Whether a dish with these tags answers the required tags, read as in <see cref="SplitTags"/>.</summary>
    public static bool FitsTags(IEnumerable<short> dishTagIds, IEnumerable<short> tagIds, IEnumerable<Domain.Entities.Tag> catalogTags)
    {
        var owned = dishTagIds.ToHashSet();
        var (all, anyOf) = SplitTags(tagIds, catalogTags);
        return all.All(owned.Contains) && anyOf.All(group => group.Any(owned.Contains));
    }

    /// <summary>
    /// Breakfast, lunch and dinner need a main dish by default. A side kind or meal role, a dish or a restaurant the user asked for
    /// at that position (trip filters included) is an explicit choice that replaces the default, e.g. drinks-only breakfast.
    /// </summary>
    public static bool RequiresMain(AgentState state, short day, string meal, IEnumerable<Domain.Entities.Tag> catalogTags,
        IEnumerable<short>? searchTagIds = null)
    {
        if (meal is not ("breakfast" or "lunch" or "dinner")) return false;
        var request = Request(state, day, meal);
        if (request.DishName is not null || request.RestaurantName is not null) return false;
        var replacing = catalogTags.Where(ReplacesMain).Select(tag => tag.Id).ToHashSet();
        return !state.TagIds.Concat(request.TagIds).Concat(searchTagIds ?? []).Any(replacing.Contains);
    }

    /// <summary>
    /// True when a catalog dish answers a requested name: the same name, or a dish of the family the name stands for, whose name holds
    /// every requested word in order ("cà ri" fits "Cơm Cà Ri Nhật Gà Chiên"). A one-word name under three letters is too broad for a family.
    /// </summary>
    public static bool FitsName(string requested, string dishName, bool ignoreAccents = true)
    {
        if (Normalize(requested) == Normalize(dishName)) return true;
        requested = DishWords(requested);
        var wanted = Words(requested);
        if (wanted.Trim().Length == 0) return false;
        // A short word with its accents ("gà", "bò", "cá") is a kind of dish the user means, matched as a whole word: "thêm món gà" was refused as too broad and
        // sent the user a list of unfiltered catalog dishes to pick from (conversation 15). Without accents a short word is too loose ("ca" is in "cà phê").
        if (Words(dishName).Contains(wanted, StringComparison.Ordinal)) return true;
        if (wanted.Trim().Length < 3) return false;
        return ignoreAccents && Words(WithoutAccents(dishName)).Contains(Words(WithoutAccents(requested)), StringComparison.Ordinal);
    }

    /// <summary>A dish name without a leading "món", which only says "a dish" ("món bò" is the kind "bò"); only "món" is dropped, since "đồ uống" without "đồ" means something else.</summary>
    public static string DishWords(string name)
    {
        var trimmed = name.Trim();
        return trimmed.StartsWith("món ", StringComparison.OrdinalIgnoreCase) && trimmed[4..].Trim() is { Length: > 0 } rest ? rest : trimmed;
    }

    private static string Words(string text) => " " + string.Join(' ', System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
        .Where(word => word.Length > 0)) + " ";

    private static string WithoutAccents(string text) => new string(text.Normalize(System.Text.NormalizationForm.FormD)
        .Where(character => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray())
        .Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');

    /// <summary>Normalizes case and spacing without treating a near name as an exact identity.</summary>
    public static string? Normalize(string? name) => name is null ? null : string.Join(' ', name.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
