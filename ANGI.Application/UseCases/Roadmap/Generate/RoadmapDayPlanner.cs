using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Roadmap.Generate;

/// <summary>
/// Pure steps of RM-10: the daily geographic cluster (step 4), the check of the AI's schedule, and the schedule the job
/// builds itself when the AI is unavailable or answers something unusable.
/// </summary>
public static class RoadmapDayPlanner
{
    /// <summary>Candidates kept around the day's anchor dish.</summary>
    public const double ClusterKm = 3;

    /// <summary>Wider cluster when the 3 km one has fewer dishes than meals to fill.</summary>
    public const double WideClusterKm = 5;

    /// <summary>
    /// The anchor is the best-ranked dish still unused; the cluster keeps the dishes whose restaurant is within 3 km of
    /// the anchor's (5 km when that leaves fewer dishes than meals), in rank order, the anchor first.
    /// </summary>
    public static IReadOnlyList<Dish> Cluster(IReadOnlyList<Dish> availableInRankOrder, int mealsToFill)
    {
        var anchor = availableInRankOrder[0];
        var near = availableInRankOrder.Where(dish => DistanceKm(anchor, dish) <= ClusterKm).ToArray();
        return near.Length >= mealsToFill
            ? near
            : availableInRankOrder.Where(dish => DistanceKm(anchor, dish) <= WideClusterKm).ToArray();
    }

    public static double DistanceKm(Dish from, Dish to) =>
        NearbyArea.DistanceKm(from.Restaurant.Latitude, from.Restaurant.Longitude, to.Restaurant.Latitude, to.Restaurant.Longitude);

    /// <summary>
    /// Accepts the AI's schedule only when it fills min(meals, cluster size) distinct meals of the request with distinct
    /// dishes of the cluster; returns it in meal order, or null so the job plans the day itself.
    /// </summary>
    public static IReadOnlyList<RoadmapPlanAssignment>? Accept(
        RoadmapPlanResult? plan,
        IReadOnlyList<MealSlot> meals,
        IReadOnlyCollection<int> clusterDishIds)
    {
        if (plan is null)
        {
            return null;
        }

        var expected = Math.Min(meals.Count, clusterDishIds.Count);
        var assignments = plan.Assignments;
        var valid = assignments.Count == expected &&
                    assignments.All(assignment => meals.Contains(assignment.MealSlot) && clusterDishIds.Contains(assignment.DishId)) &&
                    assignments.Select(assignment => assignment.MealSlot).Distinct().Count() == assignments.Count &&
                    assignments.Select(assignment => assignment.DishId).Distinct().Count() == assignments.Count;
        return valid ? assignments.OrderBy(assignment => meals.ToList().IndexOf(assignment.MealSlot)).ToArray() : null;
    }

    /// <summary>
    /// For each meal in order, the best-ranked unused cluster dish, preferring one tagged for that meal time, then one
    /// carrying a requested tag.
    /// </summary>
    public static IReadOnlyList<RoadmapPlanAssignment> Fallback(
        IReadOnlyList<Dish> clusterInRankOrder,
        IReadOnlyList<MealSlot> meals,
        IReadOnlySet<short> preferredTagIds)
    {
        var remaining = clusterInRankOrder.Select((dish, rank) => (dish, rank)).ToList();
        var assignments = new List<RoadmapPlanAssignment>();
        foreach (var meal in meals)
        {
            if (remaining.Count == 0)
            {
                break;
            }

            var code = MealTimeCode(meal);
            var best = remaining
                .OrderBy(entry => code is not null && HasMealTime(entry.dish, code) ? 0 : 1)
                .ThenBy(entry => entry.dish.DishTags.Any(dishTag => preferredTagIds.Contains(dishTag.TagId)) ? 0 : 1)
                .ThenBy(entry => entry.rank)
                .First();
            remaining.Remove(best);
            assignments.Add(new RoadmapPlanAssignment(meal, best.dish.Id));
        }

        return assignments;
    }

    /// <summary>meal_time tag code of a meal slot; late_night has none.</summary>
    public static string? MealTimeCode(MealSlot meal) => meal switch
    {
        MealSlot.Breakfast => "breakfast",
        MealSlot.Lunch => "lunch",
        MealSlot.Dinner => "dinner",
        MealSlot.Snack => "snack",
        _ => null
    };

    private static bool HasMealTime(Dish dish, string code) =>
        dish.DishTags.Any(dishTag => dishTag.Tag.TagType == TagType.MealTime && dishTag.Tag.Code == code);
}
