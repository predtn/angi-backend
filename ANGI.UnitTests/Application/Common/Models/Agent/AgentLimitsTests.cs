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
        var limits = new AgentLimits();

        limits.MessagesPerMinute.Should().Be(60);
        limits.MessagesPerDay.Should().Be(100);
    }
}
