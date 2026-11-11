using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Constraints;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Constraints;

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
    [InlineData("Đi Hà Nội 2 ngày, 1 triệu cả chuyến, phải có món Bún Chả")]
    public void Placeable_NoEveryMealWording_ReturnsRequest(string message)
    {
        // STEP-01: Create request as AgentMealRequest.
        var request = new AgentMealRequest(null, null, "Bún Chả", null, []);

        // STEP-02: Assert AgentConstraintGuard.PlaceableRequests() equals request.
        AgentConstraintGuard.PlaceableRequests(State(request), [message]).Should().Equal(request);
    }

    // TEST-02: Explicit every-meal wording naming that dish keeps the wide scope; covers "mọi bữa", "cả chuyến chỉ ăn" and unaccented wording.
    /// <summary>Keeps a user's real whole-trip request intact.</summary>
    [Theory]
    [InlineData("mọi bữa đều ăn Bún Chả")]
    [InlineData("cả chuyến chỉ ăn Bún Chả")]
    [InlineData("tat ca deu an bun cha")]
    public void Placeable_EveryMealWording_KeepsWideScope(string message)
    {
        // STEP-01: Assert AgentConstraintGuard.PlaceableRequests() is empty.
        AgentConstraintGuard.PlaceableRequests(State(new AgentMealRequest(null, null, "Bún Chả", null, [])), [message]).Should().BeEmpty();
    }

    // TEST-03: Meal-level requests and requests without a name are never placeable.
    /// <summary>Ensures only names wider than a meal are moved, so tag-only filters keep their scope.</summary>
    [Fact]
    public void Placeable_MealLevelOrUnnamed_ReturnsEmpty()
    {
        // STEP-01: Get state from State.
        var state = State(new AgentMealRequest(1, "lunch", "Bún Chả", null, []), new AgentMealRequest(null, null, null, null, [5]));

        // STEP-02: Assert AgentConstraintGuard.PlaceableRequests() is empty.
        AgentConstraintGuard.PlaceableRequests(state, ["phải có Bún Chả"]).Should().BeEmpty();
    }

    // TEST-04: Lunch comes first among declared meals; covers declared meals, a reduced declaration and legacy state without declared meals.
    /// <summary>Prefers a main mid-day meal and never adds a meal the trip did not declare.</summary>
    [Theory]
    [InlineData(new[] { "breakfast", "lunch", "dinner" }, new[] { "lunch", "dinner", "breakfast" })]
    [InlineData(new[] { "dinner", "snack" }, new[] { "dinner", "snack" })]
    [InlineData(new string[0], new[] { "lunch", "dinner", "breakfast" })]
    public void Meals_FollowPreferenceWithinDeclaredMeals(string[] declared, string[] expected)
    {
        // STEP-01: Assert AgentConstraintGuard.PlaceableMeals() equals expected.
        AgentConstraintGuard.PlaceableMeals(AgentTestData.State() with { MealSlots = declared }).Should().Equal(expected);
    }

    // TEST-05: The first free position is chosen; covers a free trip, an occupied first position, a day-level request and no free position.
    /// <summary>Keeps two named requests on different meals and a day request inside its day.</summary>
    [Fact]
    public void Slot_SkipsOccupiedPositionsAndStaysInScope()
    {
        // STEP-01: Create wide as AgentMealRequest, create occupied as AgentMealRequest and create dayLevel as AgentMealRequest.
        var wide = new AgentMealRequest(null, null, "Phở", null, []);
        var occupied = new AgentMealRequest(1, "lunch", "Bún Chả", null, []);
        var dayLevel = new AgentMealRequest(2, null, "Phở", null, []);

        // STEP-02: Assert AgentConstraintGuard.PlaceSlot() is ((short)1, "lunch"), AgentConstraintGuard.PlaceSlot() is ((short)2, "lunch") and 1 more.
        AgentConstraintGuard.PlaceSlot(State(wide), wide, ["lunch"]).Should().Be(((short)1, "lunch"));
        AgentConstraintGuard.PlaceSlot(State(wide, occupied), wide, ["lunch"]).Should().Be(((short)2, "lunch"));
        AgentConstraintGuard.PlaceSlot(State(dayLevel, occupied), dayLevel, ["lunch"]).Should().Be(((short)2, "lunch"));
        AgentConstraintGuard.PlaceSlot(State(wide) with { NumDays = 1 }, new AgentMealRequest(1, null, "Phở", null, []), []).Should().BeNull();
    }

    // TEST-06: Placing moves the name to the meal while tags and differentFromPlan keep their wide scope.
    /// <summary>Prevents placement from narrowing unrelated filters to one meal.</summary>
    [Fact]
    public void Place_MovesNameAndKeepsWideFilters()
    {
        // STEP-01: Create wide as AgentMealRequest and derive requests from Place().MealRequests.
        var wide = new AgentMealRequest(null, null, "Phở", null, [5], DifferentFromPlan: true);

        var requests = AgentConstraintGuard.PlaceRequest(State(wide), wide, 2, "dinner").MealRequests;

        // STEP-02: Assert requests is equivalent to the expected items.
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
        // STEP-01: Create placed as AgentMealRequest.
        var placed = new AgentMealRequest(1, "lunch", "Phở", null, []);

        // STEP-02: Assert AgentConstraintGuard.RetainPlacements().MealRequests equals placed and AgentConstraintGuard.RetainPlacements() is null.
        AgentConstraintGuard.RetainPlacements(State(new AgentMealRequest(null, null, "PHỞ", null, [])), [placed])!.MealRequests.Should().Equal(placed);
        AgentConstraintGuard.RetainPlacements(State(), [placed])!.MealRequests.Should().Equal(placed);
        AgentConstraintGuard.RetainPlacements(null, [placed]).Should().BeNull();
        // STEP-03: Get untouched from State.
        var untouched = State(new AgentMealRequest(null, null, "Phở", null, []));
        // STEP-04: Assert AgentConstraintGuard.RetainPlacements() is the same instance as untouched.
        AgentConstraintGuard.RetainPlacements(untouched, []).Should().BeSameAs(untouched);
    }

    // TEST-08: A later model state cannot keep the old meal of a moved request.
    /// <summary>Ensures a relocation survives the model resending the meal it was moved away from.</summary>
    [Fact]
    public void Retain_ModelResendsOldMeal_KeepsMovedMeal()
    {
        // STEP-01: Create moved as AgentMealRequest.
        var moved = new AgentMealRequest(1, "dinner", null, "Lẩu Bò", []);

        // STEP-02: Assert AgentConstraintGuard.RetainPlacements().MealRequests equals moved.
        AgentConstraintGuard.RetainPlacements(State(new AgentMealRequest(1, "lunch", null, "Lẩu Bò", [])), [moved])!.MealRequests.Should().Equal(moved);
    }

    // TEST-10: The turn-start constraints move only when they hold the same request; covers a held request and one added this turn.
    /// <summary>Ensures a placement is not treated as relaxing a constraint, and adds nothing the user never had.</summary>
    [Fact]
    public void Rebase_MovesOnlyAHeldRequest()
    {
        // STEP-01: Create wide as AgentMealRequest.
        var wide = new AgentMealRequest(null, null, "Phở", null, []);

        // STEP-02: Assert AgentConstraintGuard.RebasePlacement().MealRequests equals a new AgentMealRequest and AgentConstraintGuard.RebasePlacement().MealRequests is empty.
        AgentConstraintGuard.RebasePlacement(State(wide), wide, 1, "lunch").MealRequests.Should().Equal(new AgentMealRequest(1, "lunch", "Phở", null, []));
        AgentConstraintGuard.RebasePlacement(State(), wide, 1, "lunch").MealRequests.Should().BeEmpty();
    }
}
