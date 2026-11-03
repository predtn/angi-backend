using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies a dish or restaurant the user names is placed on one meal unless the wording explicitly asks for every meal.</summary>
public sealed class AgentNamedRequestPolicyTests
{
    /// <summary>Returns a three-day trip holding the supplied meal requests.</summary>
    private static AgentState State(params AgentMealRequest[] requests) => AgentTestData.State() with
        { NumDays = 3, MealSlots = ["breakfast", "lunch", "dinner"], MealRequests = requests };

    // TEST-01: A name without explicit every-meal wording is placeable; covers "phải có", a request with another message present and a day-level request.
    /// <summary>Ensures "must include" wording never filters every meal down to one dish.</summary>
    [Theory]
    [InlineData("nhất định phải có món Bún Chả")]
    [InlineData("Lập lịch 3 ngày")]
    [InlineData("ngày 2 có món Bún Chả nhé")]
    public void Placeable_NoEveryMealWording_ReturnsRequest(string message)
    {
        var request = new AgentMealRequest(null, null, "Bún Chả", null, []);

        AgentNamedRequestPolicy.Placeable(State(request), [message]).Should().Equal(request);
    }

    // TEST-02: Explicit every-meal wording naming that dish keeps the wide scope; covers "mọi bữa", "cả chuyến chỉ ăn" and unaccented wording.
    /// <summary>Keeps a user's real whole-trip request intact.</summary>
    [Theory]
    [InlineData("mọi bữa đều ăn Bún Chả")]
    [InlineData("cả chuyến chỉ ăn Bún Chả")]
    [InlineData("tat ca deu an bun cha")]
    public void Placeable_EveryMealWording_KeepsWideScope(string message)
    {
        AgentNamedRequestPolicy.Placeable(State(new AgentMealRequest(null, null, "Bún Chả", null, [])), [message]).Should().BeEmpty();
    }

    // TEST-03: Meal-level requests and requests without a name are never placeable.
    /// <summary>Ensures only names wider than a meal are moved, so tag-only filters keep their scope.</summary>
    [Fact]
    public void Placeable_MealLevelOrUnnamed_ReturnsEmpty()
    {
        var state = State(new AgentMealRequest(1, "lunch", "Bún Chả", null, []), new AgentMealRequest(null, null, null, null, [5]));

        AgentNamedRequestPolicy.Placeable(state, ["phải có Bún Chả"]).Should().BeEmpty();
    }

    // TEST-04: Lunch comes first among declared meals; covers declared meals, a reduced declaration and legacy state without declared meals.
    /// <summary>Prefers a main mid-day meal and never adds a meal the trip did not declare.</summary>
    [Theory]
    [InlineData(new[] { "breakfast", "lunch", "dinner" }, new[] { "lunch", "dinner", "breakfast" })]
    [InlineData(new[] { "dinner", "snack" }, new[] { "dinner", "snack" })]
    [InlineData(new string[0], new[] { "lunch", "dinner", "breakfast" })]
    public void Meals_FollowPreferenceWithinDeclaredMeals(string[] declared, string[] expected)
    {
        AgentNamedRequestPolicy.Meals(AgentTestData.State() with { MealSlots = declared }).Should().Equal(expected);
    }

    // TEST-05: The first free position is chosen; covers a free trip, an occupied first position, a day-level request and no free position.
    /// <summary>Keeps two named requests on different meals and a day request inside its day.</summary>
    [Fact]
    public void Slot_SkipsOccupiedPositionsAndStaysInScope()
    {
        var wide = new AgentMealRequest(null, null, "Phở", null, []);
        var occupied = new AgentMealRequest(1, "lunch", "Bún Chả", null, []);
        var dayLevel = new AgentMealRequest(2, null, "Phở", null, []);

        AgentNamedRequestPolicy.Slot(State(wide), wide, ["lunch"]).Should().Be(((short)1, "lunch"));
        AgentNamedRequestPolicy.Slot(State(wide, occupied), wide, ["lunch"]).Should().Be(((short)2, "lunch"));
        AgentNamedRequestPolicy.Slot(State(dayLevel, occupied), dayLevel, ["lunch"]).Should().Be(((short)2, "lunch"));
        AgentNamedRequestPolicy.Slot(State(wide) with { NumDays = 1 }, new AgentMealRequest(1, null, "Phở", null, []), []).Should().BeNull();
    }

    // TEST-06: Placing moves the name to the meal while tags and differentFromPlan keep their wide scope.
    /// <summary>Prevents placement from narrowing unrelated filters to one meal.</summary>
    [Fact]
    public void Place_MovesNameAndKeepsWideFilters()
    {
        var wide = new AgentMealRequest(null, null, "Phở", null, [5], DifferentFromPlan: true);

        var requests = AgentNamedRequestPolicy.Place(State(wide), wide, 2, "dinner").MealRequests;

        requests.Should().BeEquivalentTo(new[]
        {
            new AgentMealRequest(null, null, null, null, [5], DifferentFromPlan: true),
            new AgentMealRequest(2, "dinner", "Phở", null, [])
        });
    }

    // TEST-07: A later model state keeps the placement; covers a resent wide request, a state without the placed request, null state and no placement.
    /// <summary>Ensures the final model output cannot widen or silently drop a backend placement.</summary>
    [Fact]
    public void Retain_ModelResendsOrDrops_KeepsPlacement()
    {
        var placed = new AgentMealRequest(1, "lunch", "Phở", null, []);

        AgentNamedRequestPolicy.Retain(State(new AgentMealRequest(null, null, "PHỞ", null, [])), [placed])!.MealRequests.Should().Equal(placed);
        AgentNamedRequestPolicy.Retain(State(), [placed])!.MealRequests.Should().Equal(placed);
        AgentNamedRequestPolicy.Retain(null, [placed]).Should().BeNull();
        var untouched = State(new AgentMealRequest(null, null, "Phở", null, []));
        AgentNamedRequestPolicy.Retain(untouched, []).Should().BeSameAs(untouched);
    }

    // TEST-08: A later model state cannot keep the old meal of a moved request.
    /// <summary>Ensures a relocation survives the model resending the meal it was moved away from.</summary>
    [Fact]
    public void Retain_ModelResendsOldMeal_KeepsMovedMeal()
    {
        var moved = new AgentMealRequest(1, "dinner", null, "Lẩu Bò", []);

        AgentNamedRequestPolicy.Retain(State(new AgentMealRequest(1, "lunch", null, "Lẩu Bò", [])), [moved])!.MealRequests.Should().Equal(moved);
    }

    // TEST-09: Only a meal the user did not name may move; covers a backend placement, a meal the user named in any message and an unnamed request.
    /// <summary>Keeps an explicit "bữa trưa ngày 1 ăn ở X" from being silently moved to dinner.</summary>
    [Fact]
    public void Relocatable_OnlyMealsTheUserDidNotName()
    {
        var placedByBackend = new AgentMealRequest(1, "lunch", null, "Lẩu Bò", []);
        var named = new AgentMealRequest(2, "lunch", null, "Lẩu Gà", []);
        var state = State(placedByBackend, named, new AgentMealRequest(3, "lunch", null, null, [5]));

        AgentNamedRequestPolicy.Relocatable(state, ["nhất định phải ăn ở Lẩu Bò", "bữa trưa ngày 2 ăn ở Lẩu Gà"]).Should().Equal(placedByBackend);
    }

    // TEST-10: The turn-start constraints move only when they hold the same request; covers a held request and one added this turn.
    /// <summary>Ensures a placement is not treated as relaxing a constraint, and adds nothing the user never had.</summary>
    [Fact]
    public void Rebase_MovesOnlyAHeldRequest()
    {
        var wide = new AgentMealRequest(null, null, "Phở", null, []);

        AgentNamedRequestPolicy.Rebase(State(wide), wide, 1, "lunch").MealRequests.Should().Equal(new AgentMealRequest(1, "lunch", "Phở", null, []));
        AgentNamedRequestPolicy.Rebase(State(), wide, 1, "lunch").MealRequests.Should().BeEmpty();
    }
}
