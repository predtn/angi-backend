using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Constraints;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Constraints;

/// <summary>Verifies explicit day/meal wording is read deterministically and keeps a meal taste on that meal.</summary>
public sealed class AgentMealScopePolicyTests
{
    // TEST-01: A meal and a day are paired in either order with default breakfast/lunch/dinner numbering; covers bữa 2 ngày 1,
    // bữa trưa của ngày 1, ngày 1 bữa 2, "ngày 1, bữa tối", bữa thứ hai ngày 3, bữa đầu tiên ngày 2 and bữa cuối ngày 4.
    /// <summary>Checks every supported wording resolves to the same day/meal scope a person would mean.</summary>
    [Theory]
    [InlineData("Vừa, áp dụng cho bữa 2 ngày 1", 1, "lunch")]
    [InlineData("Áp dụng cho bữa trưa của ngày 1", 1, "lunch")]
    [InlineData("Ngày 1 bữa 2 nhé", 1, "lunch")]
    [InlineData("Ngày 1, bữa tối", 1, "dinner")]
    [InlineData("Bữa thứ hai ngày 3", 3, "lunch")]
    [InlineData("Bữa đầu tiên ngày 2", 2, "breakfast")]
    [InlineData("Bữa cuối ngày 4", 4, "dinner")]
    public void Read_MealWithDay_ResolvesScope(string content, short day, string meal)
    {
        // STEP-01: Read references from the response; assert references.Meals equals a new AgentScope, references.Days is empty and references.OutOfRange is false.
        var references = AgentMealScopePolicy.Read(content, new AgentState());

        references.Meals.Should().Equal(new AgentScope(day, meal));
        references.Days.Should().BeEmpty();
        references.OutOfRange.Should().BeFalse();
    }

    // TEST-02: "Bữa N" counts the declared meals in breakfast-to-late-night order; covers two declared meals and a declared snack as the last meal.
    /// <summary>Ensures numbering follows the plan's own meals instead of a fixed breakfast/lunch/dinner list.</summary>
    [Theory]
    [InlineData(new[] { "dinner", "breakfast" }, "bữa 2 ngày 1", "dinner")]
    [InlineData(new[] { "snack", "lunch", "dinner" }, "bữa cuối ngày 1", "snack")]
    public void Read_DeclaredMeals_DefineNumbering(string[] mealSlots, string content, string meal)
    {
        // STEP-01: Assert AgentMealScopePolicy.Read().Meals equals a new AgentScope.
        AgentMealScopePolicy.Read(content, new AgentState { MealSlots = mealSlots }).Meals.Should().Equal(new AgentScope(1, meal));
    }

    // TEST-03: A day named without a meal is reported separately from a meal paired with a day, and a duration is not a day reference.
    /// <summary>Separates "cả ngày 3" from "bữa 2 ngày 1" so a whole-day taste is not narrowed by mistake.</summary>
    [Fact]
    public void Read_StandaloneDay_IsReportedSeparately()
    {
        // STEP-01: Read references from the response; assert references.Meals equals a new AgentScope and references.Days equals 3.
        var references = AgentMealScopePolicy.Read("Hà Nội 4 ngày, chua cho cả ngày 3 và bữa 2 ngày 1", new AgentState());

        references.Meals.Should().Equal(new AgentScope(1, "lunch"));
        references.Days.Should().Equal((short)3);
    }
}
