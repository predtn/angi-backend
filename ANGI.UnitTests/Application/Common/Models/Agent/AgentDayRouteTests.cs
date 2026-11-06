using ANGI.Application.Common.Models.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Common.Models.Agent;

/// <summary>Verifies durable route totals independently of any transport or routing provider.</summary>
public sealed class AgentDayRouteTests
{
    // TEST-01: Route totals survive JSON round trips and do not overflow when maximum-sized legs are accumulated.
    /// <summary>Verifies the preview's required distance/time totals and durable validation serialization.</summary>
    [Fact]
    public void Totals_AreDurableAndOverflowSafe()
    {
        // STEP-01: Create route as AgentDayRoute.
        var route = new AgentDayRoute(1, true, false, [new(int.MaxValue, int.MaxValue), new(int.MaxValue, int.MaxValue)]);
        // STEP-02: Read restored from the response; assert restored.TotalDistanceMeters is 2L * int.MaxValue and restored.TotalDurationSeconds is 2L * int.MaxValue.
        var restored = AgentJson.Read<AgentDayRoute>(AgentJson.Write(route));
        restored.TotalDistanceMeters.Should().Be(2L * int.MaxValue); restored.TotalDurationSeconds.Should().Be(2L * int.MaxValue);
    }
}
