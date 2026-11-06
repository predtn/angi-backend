using ANGI.Application.UseCases.Agent;
using ANGI.UnitTests.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Agent;

/// <summary>Verifies which state values count as a change to the trip's constraints.</summary>
public sealed class AgentConversationRulesTests
{
    // TEST-01: The mark of an offered dish is not a constraint, so it never makes a ready proposal look out of date; a real change still does.
    /// <summary>A suggestion must not stop the user from saving the plan they already have.</summary>
    [Fact]
    public void SameConstraints_OfferedDishMark_IsIgnored()
    {
        // STEP-01: Get state from AgentTestData.State.
        var state = AgentTestData.State();

        // STEP-02: Assert AgentConversationRules.SameConstraints() ignores the mark and sees a changed budget.
        AgentConversationRules.SameConstraints(state with { DishOffered = true }, state).Should().BeTrue();
        AgentConversationRules.SameConstraints(state with { BudgetAmount = (state.BudgetAmount ?? 0) + 1 }, state).Should().BeFalse();
    }
}
