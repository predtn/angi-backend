using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Constraints;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Constraints;

/// <summary>Verifies what counts as a change of the user's conditions, and that a name the catalog did not settle is not one.</summary>
public sealed class AgentChangeRequestPolicyTests
{
    // TEST-01: Backend-owned or model-written fields are not a change of the user's conditions, while a taste target is; covers a profile snapshot, a note, a title and a taste target.
    /// <summary>Compares only user constraints, so profile initialization during ranking or a model note never counts as a request.</summary>
    [Theory]
    [InlineData("preferences", true)]
    [InlineData("note", true)]
    [InlineData("title", true)]
    [InlineData("taste", false)]
    public void SameUserConstraints_CountsOnlyUserConstraints(string area, bool same)
    {
        // STEP-01: Get before from AgentTestData.State and derive after from the expected value.
        var before = AgentTestData.State();
        var after = area switch
        {
            "preferences" => before with { Preferences = new() { Initialized = true, BaselineVector = Enumerable.Repeat(.5, 12).ToArray() } },
            "note" => before with { Note = "ăn vui vẻ" },
            "title" => before with { Title = "Lịch vui vẻ" },
            _ => before with { TasteTargets = [new("spicy", .25)] }
        };

        // STEP-02: Assert AgentConstraintGuard.SameUserConstraints() is same.
        AgentConstraintGuard.SameUserConstraints(before, after).Should().Be(same);
    }

    // TEST-02: A turn that ends on a catalog question drops the dish/restaurant names it added and keeps their tags, while a name already
    // in the turn-start state stays; covers a new unfound restaurant with a tag, a new unfound dish alone and an earlier restaurant.
    /// <summary>Keeps an unsettled name out of the state so the user can name another dish or restaurant without a change confirmation.</summary>
    [Fact]
    public void WithoutUnsettledNames_DropsOnlyNamesAddedThisTurn()
    {
        // STEP-01: Derive before from a copy of AgentTestData.State(), derive after from a copy of before and 1 more.
        var before = AgentTestData.State() with { MealRequests = [new(2, "lunch", null, "Lẩu Bò Mai", [], RestaurantId: 575)] };
        var after = before with { MealRequests = [new(2, "lunch", null, "Lẩu Bò Mai", [], RestaurantId: 575),
            new(1, "dinner", null, "Hương Sen Phố Cổ", [53]), new(1, "lunch", "Phở Không Có", null, [])] };

        var result = AgentConstraintGuard.WithoutUnsettledNames(after, before);

        // STEP-02: Assert result.MealRequests has 2 item(s), result.MealRequests[0] is the same instance as after.MealRequests[0] and 1 more.
        result.MealRequests.Should().HaveCount(2);
        result.MealRequests[0].Should().BeSameAs(after.MealRequests[0]);
        result.MealRequests[1].Should().Match<AgentMealRequest>(request => request.DayNumber == 1 && request.MealSlot == "dinner" &&
            request.RestaurantName == null && request.DishName == null && request.TagIds.Single() == 53);
    }
}
