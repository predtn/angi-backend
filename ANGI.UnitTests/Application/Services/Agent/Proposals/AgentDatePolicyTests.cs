using FluentAssertions;
using ANGI.Application.Services.Agent.Proposals;
using ANGI.Application.Services.Agent.Conversation;

namespace ANGI.UnitTests.Application.Services.Agent.Proposals;

/// <summary>Verifies AgentConfirmationPolicy reads only dates the user really stated, relative to a fixed Tuesday 3 November 2026.</summary>
public sealed class AgentDatePolicyTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);

    // TEST-01: Today follows Vietnam time around midnight; covers one tick before, exactly at and after 17:00 UTC.
    /// <summary>A traveler choosing "hôm nay" at 00:30 in Hanoi must get the new date although UTC is still the day before.</summary>
    [Theory]
    [InlineData("2026-11-03T16:59:59Z", 2026, 11, 3)]
    [InlineData("2026-11-03T17:00:00Z", 2026, 11, 4)]
    [InlineData("2026-11-03T17:00:01Z", 2026, 11, 4)]
    public void Today_UsesVietnamTime(string now, int year, int month, int day)
    {
        // STEP-01: Create clock as FixedClock.
        var clock = new FixedClock(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));

        // STEP-02: Assert AgentConfirmationPolicy.Today() is a new DateOnly.
        AgentConfirmationPolicy.Today(clock).Should().Be(new DateOnly(year, month, day));
    }

    /// <summary>Stops time at a chosen instant.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        /// <summary>Returns the chosen instant.</summary>
        public override DateTimeOffset GetUtcNow() => now;
    }
}
