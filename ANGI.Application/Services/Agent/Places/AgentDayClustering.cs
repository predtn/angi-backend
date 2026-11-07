namespace ANGI.Application.Services.Agent.Places;

/// <summary>
/// Keeps the meals of one day close together. Reco ranks every meal on its own, so the top dish of breakfast, lunch and dinner can sit
/// at opposite ends of a province. For each day this picks one dish per meal, walking from meal to meal: the next meal takes its
/// best-ranked dish within <c>legKm</c> of the previous one, or the nearest dish when none is that close. Only catalog coordinates
/// and rank order are read, never the user's words.
/// </summary>
public static class AgentDayClustering
{
    /// <summary>Straight-line distance a meal may sit from the previous one before rank gives way to distance.</summary>
    public const double DefaultLegKm = 5;
    public const double MinLegKm = 0.5;
    public const double MaxLegKm = 30;
    // Thử vài món đầu của bữa đầu tiên làm điểm neo: món hạng 1 có thể nằm xa mọi món hợp khẩu vị của bữa sau.
    private const int AnchorTries = 5;
    // Chỉ xét phần đầu danh sách reco: sâu hơn là món kém hợp khẩu vị, gần mấy cũng không nên lấy.
    private const int Depth = 30;
    // Mỗi km vượt legKm tính bằng 3 bậc hạng: chặng xa phải đổi bằng món hợp khẩu vị hơn hẳn.
    private const double KmPenalty = 3;

    public sealed record Option(int DishId, string Name, double Latitude, double Longitude);

    public sealed record MealPick(string MealSlot, Option Dish);

    /// <summary>One day's picks; StartKm is the distance from the day's starting point to the first meal, when there is one.</summary>
    public sealed record DayPlan(short DayNumber, MealPick[] Meals, double[] LegsKm, double? StartKm = null);

    /// <summary>
    /// One dish per meal for each day, in meal order; names already used on another day are avoided while others remain. When a day has a
    /// starting point (the user's current location, a point pinned on the map, or the place the user named for that day) the first meal
    /// is also kept within legKm of it, so the walk starts where the user is.
    /// </summary>
    public static DayPlan[] Arrange(IReadOnlyList<short> days, IReadOnlyList<string> meals,
        Func<short, string, IReadOnlyList<Option>> candidates, double legKm, ISet<string> usedNames,
        Func<short, (double Latitude, double Longitude)?>? start = null)
    {
        var plans = new List<DayPlan>();
        foreach (var day in days)
        {
            var dayMeals = meals.Where(meal => candidates(day, meal).Count > 0).ToArray();
            if (dayMeals.Length == 0) continue;
            (MealPick[] Picks, double Cost)? best = null;
            var from = start?.Invoke(day);
            // Có điểm xuất phát thì xét sâu hơn ở bữa đầu: món gần người dùng có thể đứng dưới vài món đầu bảng.
            foreach (var anchor in Usable(candidates(day, dayMeals[0]), usedNames, new HashSet<string>()).Take(from is null ? AnchorTries : Depth))
            {
                var chain = Chain(day, dayMeals, anchor, candidates, legKm, usedNames);
                var cost = chain.Cost + (from is { } point ? Math.Max(0, Km(point.Latitude, point.Longitude, anchor.Latitude, anchor.Longitude) - legKm) * KmPenalty : 0);
                if (best is null || cost < best.Value.Cost) best = (chain.Picks, cost);
            }
            if (best is not { } chosen) continue;
            usedNames.UnionWith(chosen.Picks.Select(pick => pick.Dish.Name));
            var first = chosen.Picks[0].Dish;
            plans.Add(new(day, chosen.Picks, Legs(chosen.Picks.Select(pick => pick.Dish).ToArray()),
                from is { } origin ? Math.Round(Km(origin.Latitude, origin.Longitude, first.Latitude, first.Longitude), 1) : null));
        }
        return [.. plans];
    }

    /// <summary>
    /// Orders one meal's ranked dishes for an edit of that meal alone: dishes within <c>legKm</c> of every other meal kept that day come
    /// first in rank order, then the rest from nearest to farthest. With nothing kept that day the rank order is unchanged.
    /// </summary>
    public static T[] NearNeighbours<T>(IReadOnlyList<T> ranked, Func<T, (double Latitude, double Longitude)?> position,
        IReadOnlyList<(double Latitude, double Longitude)> neighbours, double legKm)
    {
        if (neighbours.Count == 0) return [.. ranked];
        double Farthest(T item) => position(item) is { } at ? neighbours.Max(other => Km(at.Latitude, at.Longitude, other.Latitude, other.Longitude)) : double.MaxValue;
        var near = ranked.Where(item => Farthest(item) <= legKm);
        var far = ranked.Where(item => Farthest(item) > legKm).OrderBy(Farthest);
        return [.. near, .. far];
    }

    public static double Km(double lat1, double lng1, double lat2, double lng2)
    {
        static double Rad(double degrees) => degrees * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLng = Rad(lng2 - lng1);
        var h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Pow(Math.Sin(dLng / 2), 2);
        return 2 * 6371 * Math.Asin(Math.Sqrt(h));
    }

    private static (MealPick[] Picks, double Cost) Chain(short day, string[] meals, Option anchor,
        Func<short, string, IReadOnlyList<Option>> candidates, double legKm, ISet<string> usedNames)
    {
        var picks = new List<MealPick> { new(meals[0], anchor) };
        var cost = (double)IndexOf(candidates(day, meals[0]), anchor);
        foreach (var meal in meals.Skip(1))
        {
            var previous = picks[^1].Dish;
            var options = Usable(candidates(day, meal), usedNames, picks.Select(pick => pick.Dish.Name).ToHashSet()).Take(Depth).ToArray();
            if (options.Length == 0) continue;
            var close = options.FirstOrDefault(option => Distance(previous, option) <= legKm);
            var next = close ?? options.MinBy(option => Distance(previous, option))!;
            cost += IndexOf(candidates(day, meal), next) + Math.Max(0, Distance(previous, next) - legKm) * KmPenalty;
            picks.Add(new(meal, next));
        }
        return ([.. picks], cost);
    }

    /// <summary>Dishes whose name is new to the trip and the day; a meal that has none of those falls back to repeats rather than staying empty.</summary>
    private static IEnumerable<Option> Usable(IReadOnlyList<Option> options, ISet<string> usedNames, ISet<string> today)
    {
        var fresh = options.Where(option => !usedNames.Contains(option.Name) && !today.Contains(option.Name)).ToArray();
        if (fresh.Length > 0) return fresh;
        var notToday = options.Where(option => !today.Contains(option.Name)).ToArray();
        return notToday.Length > 0 ? notToday : options;
    }

    private static int IndexOf(IReadOnlyList<Option> options, Option option)
    {
        for (var index = 0; index < options.Count; index++) if (options[index].DishId == option.DishId) return index;
        return options.Count;
    }

    private static double Distance(Option from, Option to) => Km(from.Latitude, from.Longitude, to.Latitude, to.Longitude);

    private static double[] Legs(Option[] dishes) => dishes.Skip(1).Select((dish, index) => Math.Round(Distance(dishes[index], dish), 1)).ToArray();
}
