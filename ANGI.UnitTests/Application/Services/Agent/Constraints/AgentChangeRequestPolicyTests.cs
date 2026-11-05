using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Constraints;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Constraints;

/// <summary>Verifies a plan change is treated as unrequested only when the message asks for nothing and no grounded user constraint changed.</summary>
public sealed class AgentChangeRequestPolicyTests
{
    private static readonly Dictionary<short, string> Tags = new() { [6] = "Món ăn vặt", [11] = "Phở", [53] = "Món chay" };

    // TEST-01: A message with no change wording, taste, day/meal or new constraint is unrequested; covers "ăn vui vui", "ok" and "cảm ơn nhé".
    /// <summary>Identifies the vague messages that must never silently rewrite an existing plan.</summary>
    [Theory]
    [InlineData("ăn vui vui")]
    [InlineData("ok")]
    [InlineData("cảm ơn nhé")]
    public void IsUnrequested_NothingAsked_IsTrue(string content)
    {
        // STEP-01: Get state from AgentTestData.State.
        var state = AgentTestData.State();

        // STEP-02: Assert AgentChangeRequestPolicy.IsUnrequested() is true.
        AgentChangeRequestPolicy.IsUnrequested(content, state, state, Tags).Should().BeTrue();
    }

    // TEST-02: Change wording, a taste, a day or meal reference keeps the change requested; covers "Plan lunch", "đổi", "phương án khác",
    // "làm lại", a taste word, "ngày 2" and "bữa tối".
    /// <summary>Keeps every real edit request from being blocked by the vague-message rule.</summary>
    [Theory]
    [InlineData("Plan lunch")]
    [InlineData("đổi giúp tôi")]
    [InlineData("cho tôi phương án khác")]
    [InlineData("làm lại đi")]
    [InlineData("cay hơn chút")]
    [InlineData("ngày 2 thì sao")]
    [InlineData("bữa tối nhé")]
    public void IsUnrequested_ChangeSignal_IsFalse(string content)
    {
        // STEP-01: Get state from AgentTestData.State.
        var state = AgentTestData.State();

        // STEP-02: Assert AgentChangeRequestPolicy.IsUnrequested() is false.
        AgentChangeRequestPolicy.IsUnrequested(content, state, state, Tags).Should().BeFalse();
    }

    // TEST-03: Backend-owned or model-written fields never count as a request, and a new taste target is left to the constraint layer;
    // covers a profile snapshot, a note, a title and a taste target.
    /// <summary>Compares only user constraints, so profile initialization during ranking or a model note never counts as a request.</summary>
    [Theory]
    [InlineData("preferences", true)]
    [InlineData("note", true)]
    [InlineData("title", true)]
    [InlineData("taste", false)]
    public void IsUnrequested_StateDifference_CountsOnlyUserConstraints(string area, bool unrequested)
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

        // STEP-02: Assert AgentChangeRequestPolicy.IsUnrequested() is unrequested.
        AgentChangeRequestPolicy.IsUnrequested("ăn vui vui", before, after, Tags).Should().Be(unrequested);
    }

    // TEST-04: A tag or dish the model adds counts as requested only when the message names it; covers a "Món ăn vặt" tag invented from
    // "ăn vui vui", a dish name invented from it, "tôi muốn ăn phở" with the Phở tag, "ăn đồ ăn vặt thôi" with the snack tag and a named dish.
    /// <summary>Stops a vague message from smuggling invented hard constraints into the plan, while real named requests still pass.</summary>
    [Theory]
    [InlineData("ăn vui vui", "tag", (short)6, null, true)]
    [InlineData("ăn vui vui", "dish", (short)0, "Phở Gà", true)]
    [InlineData("tôi muốn ăn phở", "tag", (short)11, null, false)]
    [InlineData("ăn đồ ăn vặt thôi", "tag", (short)6, null, false)]
    [InlineData("cho tôi Phở Gà Trộn Măng", "dish", (short)0, "Phở Gà Trộn Măng", false)]
    public void IsUnrequested_AddedConstraint_MustBeGrounded(string content, string kind, short tagId, string? dishName, bool unrequested)
    {
        // STEP-01: Derive before from a copy of AgentTestData.State() and derive after from a copy of before.
        var before = AgentTestData.State() with { MealSlots = ["lunch"] };
        var after = before with { MealRequests = [kind == "tag" ? new(1, "lunch", null, null, [tagId]) : new(1, "lunch", dishName, null, [])] };

        // STEP-02: Assert AgentChangeRequestPolicy.IsUnrequested() is unrequested.
        AgentChangeRequestPolicy.IsUnrequested(content, before, after, Tags).Should().Be(unrequested);
    }

    // TEST-05: A turn that ends on a catalog question drops the dish/restaurant names it added and keeps their tags, while a name already
    // in the turn-start state stays; covers a new unfound restaurant with a tag, a new unfound dish alone and an earlier restaurant.
    /// <summary>Keeps an unsettled name out of the state so the user can name another dish or restaurant without a change confirmation.</summary>
    [Fact]
    public void WithoutUnsettledNames_DropsOnlyNamesAddedThisTurn()
    {
        // STEP-01: Derive before from a copy of AgentTestData.State(), derive after from a copy of before and 1 more.
        var before = AgentTestData.State() with { MealRequests = [new(2, "lunch", null, "Lẩu Bò Mai", [], RestaurantId: 575)] };
        var after = before with { MealRequests = [new(2, "lunch", null, "Lẩu Bò Mai", [], RestaurantId: 575),
            new(1, "dinner", null, "Hương Sen Phố Cổ", [53]), new(1, "lunch", "Phở Không Có", null, [])] };

        var result = AgentChangeRequestPolicy.WithoutUnsettledNames(after, before);

        // STEP-02: Assert result.MealRequests has 2 item(s), result.MealRequests[0] is the same instance as after.MealRequests[0] and 1 more.
        result.MealRequests.Should().HaveCount(2);
        result.MealRequests[0].Should().BeSameAs(after.MealRequests[0]);
        result.MealRequests[1].Should().Match<AgentMealRequest>(request => request.DayNumber == 1 && request.MealSlot == "dinner" &&
            request.RestaurantName == null && request.DishName == null && request.TagIds.Single() == 53);
    }
}
