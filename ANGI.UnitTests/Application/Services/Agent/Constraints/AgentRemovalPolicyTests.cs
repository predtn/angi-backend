using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Constraints;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Constraints;

/// <summary>Verifies that meals a patch empties are recorded as skipped.</summary>
public sealed class AgentRemovalPolicyTests
{
    // TEST-01: Emptied meals are recorded, a meal with a dish again is dropped, and an empty result stays null so state has no empty field.
    /// <summary>Keeps skipped meals exact so a re-added meal is required again after later removals.</summary>
    [Fact]
    public void WithSkippedMeals_TracksOnlyEmptyMeals()
    {
        // STEP-01: Get source from AgentTestData.Snapshot and derive state from a copy of AgentTestData.State().
        var source = AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1, "lunch"), AgentTestData.Item(2, "b", 1, "dinner"));
        var state = AgentTestData.State() with { SkippedMeals = [new(2, "lunch"), new(1, "dinner")] };

        // STEP-02: Assert WithSkippedMeals().SkippedMeals is equivalent to the expected items and AgentConstraintGuard.Uncovered() is null.
        AgentConstraintGuard.WithSkippedMeals(state, source, [AgentTestData.Item(2, "b", 1, "dinner")]).SkippedMeals
            .Should().BeEquivalentTo(new[] { new AgentScope(2, "lunch"), new AgentScope(1, "lunch") });
        AgentConstraintGuard.Uncovered([new(1, "lunch")], [AgentTestData.Item(1, "a", 1, "lunch")]).Should().BeNull();
    }

}
