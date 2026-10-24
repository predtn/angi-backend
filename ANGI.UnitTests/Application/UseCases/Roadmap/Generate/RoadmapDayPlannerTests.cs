using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.UseCases.Roadmap.Generate;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Roadmap.Generate;

public sealed class RoadmapDayPlannerTests
{
    // About 0.009° of latitude per km.
    private static Dish Dish(int id, double kmNorth, params (TagType Type, short Id, string Code)[] tags)
    {
        var dish = new Dish
        {
            Id = id,
            Name = $"Món {id}",
            Restaurant = new Domain.Entities.Restaurant { Latitude = 16.0m + (decimal)(kmNorth * 0.009), Longitude = 108.2m }
        };
        foreach (var (type, tagId, code) in tags)
        {
            dish.DishTags.Add(new DishTag { TagId = tagId, Tag = new Tag { Id = tagId, TagType = type, Code = code, Name = code } });
        }

        return dish;
    }

    // TEST-01: Keep dishes within 3 km of the anchor, or 5 km when 3 km has fewer dishes than meals.
    [Fact]
    public void Cluster_ShouldUseThreeKmThenFiveKmAroundTheBestDish()
    {
        var ranked = new[] { Dish(1, 0), Dish(2, 2.5), Dish(3, 4), Dish(4, 8) };

        RoadmapDayPlanner.Cluster(ranked, 2).Select(dish => dish.Id).Should().Equal(1, 2);
        RoadmapDayPlanner.Cluster(ranked, 3).Select(dish => dish.Id).Should().Equal(1, 2, 3);
        RoadmapDayPlanner.Cluster(ranked, 5).Select(dish => dish.Id).Should().Equal(1, 2, 3);
    }

    // TEST-02: Accept a schedule that fills each meal once with distinct cluster dishes, in meal order.
    [Fact]
    public void Accept_WithValidPlan_ShouldReturnItInMealOrder()
    {
        var meals = new[] { MealSlot.Breakfast, MealSlot.Dinner };
        var plan = new RoadmapPlanResult("gpt", [new(MealSlot.Dinner, 2), new(MealSlot.Breakfast, 1)]);

        RoadmapDayPlanner.Accept(plan, meals, [1, 2, 3]).Should().Equal(
            new RoadmapPlanAssignment(MealSlot.Breakfast, 1), new RoadmapPlanAssignment(MealSlot.Dinner, 2));
    }

    // TEST-03: Refuse a missing meal, a repeated dish or meal, a dish outside the cluster, or no plan at all.
    [Theory]
    [MemberData(nameof(InvalidPlans))]
    public void Accept_WithUnusablePlan_ShouldReturnNull(RoadmapPlanResult? plan)
    {
        RoadmapDayPlanner.Accept(plan, [MealSlot.Breakfast, MealSlot.Dinner], [1, 2, 3]).Should().BeNull();
    }

    public static TheoryData<RoadmapPlanResult?> InvalidPlans() => new()
    {
        null,
        new RoadmapPlanResult("gpt", [new(MealSlot.Breakfast, 1)]),
        new RoadmapPlanResult("gpt", [new(MealSlot.Breakfast, 1), new(MealSlot.Dinner, 1)]),
        new RoadmapPlanResult("gpt", [new(MealSlot.Breakfast, 1), new(MealSlot.Breakfast, 2)]),
        new RoadmapPlanResult("gpt", [new(MealSlot.Breakfast, 1), new(MealSlot.Dinner, 9)]),
        new RoadmapPlanResult("gpt", [new(MealSlot.Breakfast, 1), new(MealSlot.Lunch, 2)])
    };

    // TEST-04: A cluster smaller than the meals only needs one dish per dish available.
    [Fact]
    public void Accept_WithSmallCluster_ShouldExpectOnlyAsManyMealsAsDishes()
    {
        var plan = new RoadmapPlanResult("gpt", [new(MealSlot.Lunch, 3)]);

        RoadmapDayPlanner.Accept(plan, [MealSlot.Breakfast, MealSlot.Lunch], [3]).Should().ContainSingle();
    }

    // TEST-05: The fallback prefers the meal-time tag, then a requested tag, then the reco rank, never repeating a dish.
    [Fact]
    public void Fallback_ShouldPreferMealTimeThenRequestedTagThenRank()
    {
        var cluster = new[]
        {
            Dish(1, 0, (TagType.MealTime, 2, "dinner")),
            Dish(2, 0, (TagType.Category, 9, "soup")),
            Dish(3, 0, (TagType.MealTime, 1, "breakfast")),
            Dish(4, 0)
        };

        var plan = RoadmapDayPlanner.Fallback(cluster, [MealSlot.Breakfast, MealSlot.Lunch, MealSlot.Dinner, MealSlot.LateNight], new HashSet<short> { 9 });

        plan.Should().Equal(
            new RoadmapPlanAssignment(MealSlot.Breakfast, 3),
            new RoadmapPlanAssignment(MealSlot.Lunch, 2),
            new RoadmapPlanAssignment(MealSlot.Dinner, 1),
            new RoadmapPlanAssignment(MealSlot.LateNight, 4));
        RoadmapDayPlanner.Fallback(cluster.Take(1).ToArray(), [MealSlot.Breakfast, MealSlot.Lunch], new HashSet<short>()).Should().ContainSingle();
    }
}
