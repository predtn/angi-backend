using ANGI.Application.Common.Models.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Common.Models.Agent;

/// <summary>Verifies the Agent limits a deployment gets when configuration does not override them.</summary>
public sealed class AgentLimitsTests
{
    // TEST-01: The per-user chat limit defaults to 60 messages a minute and keeps its separate daily limit.
    /// <summary>Ensures the default admission limit stays in line with appsettings.json.</summary>
    [Fact]
    public void Defaults_AllowSixtyMessagesPerMinute()
    {
        // STEP-01: Create limits as AgentLimits.
        var limits = new AgentLimits();

        // STEP-02: Assert limits.MessagesPerMinute is 60 and limits.MessagesPerDay is 100.
        limits.MessagesPerMinute.Should().Be(60);
        limits.MessagesPerDay.Should().Be(100);
    }
}
