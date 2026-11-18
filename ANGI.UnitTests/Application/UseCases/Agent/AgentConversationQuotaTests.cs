using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.UseCases.Agent;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Agent;

/// <summary>Verifies the message allowance shown above the Agent chat box.</summary>
public sealed class AgentConversationQuotaTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
    private readonly Mock<IAgentRepository> repository = new();
    private readonly AgentLimits limits = new() { MessagesPerDay = 60 };

    // TEST-01: An unused allowance is full and has no time to wait for.
    /// <summary>Does not look up an oldest attempt when none is counted.</summary>
    [Fact]
    public async Task QuotaAsync_NoAttempts_IsFullWithoutResetTime()
    {
        // STEP-01: Read the quota with the default count of zero.
        var quota = await AgentConversationRules.QuotaAsync(repository.Object, 3, Now, limits, default);

        // STEP-02: Assert the full allowance and that the oldest attempt was not queried.
        quota.Should().Be(new ANGI.Application.DTOs.Agent.AgentQuotaDto(60, 60));
        repository.Verify(x => x.GetOldestAttemptAsync(It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-02: Messages come back 24 hours after the oldest counted attempt; covers a partly used and an overspent allowance.
    /// <summary>Uses the same 24-hour window as admission and never reports a negative balance.</summary>
    [Theory]
    [InlineData(1, 59)]
    [InlineData(75, 0)]
    public async Task QuotaAsync_WithAttempts_ReturnsRemainingAndNextReturn(int used, int remaining)
    {
        // STEP-01: Stub the count and the oldest attempt inside the window.
        var since = Now.AddDays(-1); var oldest = Now.AddHours(-20);
        repository.Setup(x => x.CountAttemptsAsync(3, since, It.IsAny<CancellationToken>())).ReturnsAsync(used);
        repository.Setup(x => x.GetOldestAttemptAsync(3, since, It.IsAny<CancellationToken>())).ReturnsAsync(oldest);

        // STEP-02: Read the quota.
        var quota = await AgentConversationRules.QuotaAsync(repository.Object, 3, Now, limits, default);

        // STEP-03: Assert the balance and the time the oldest attempt leaves the window.
        quota.Remaining.Should().Be(remaining);
        quota.ResetsAt.Should().Be(oldest.AddDays(1));
    }
}
