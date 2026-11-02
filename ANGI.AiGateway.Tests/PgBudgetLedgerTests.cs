using ANGI.AiGateway.Storage;
using Npgsql;
using Xunit;

namespace ANGI.AiGateway.Tests;

/// <summary>Runs a ledger test only against an explicitly configured isolated *_test PostgreSQL database.</summary>
public sealed class GatewayPostgresFactAttribute : FactAttribute
{
    public GatewayPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANGI_GATEWAY_TEST_CONNECTION")))
            Skip = "Set ANGI_GATEWAY_TEST_CONNECTION to an isolated *_test PostgreSQL database.";
    }
}

// The whole class runs sequentially. Each test touches only ai_control inside an explicitly named test database.
/// <summary>Verifies the PostgreSQL ledger serializes admissions, enforces every cap and keeps uncertain costs charged.</summary>
public sealed class PgBudgetLedgerTests
{
    /// <summary>Builds isolated test settings; the per-minute limit is configurable because it is part of the policy hash.</summary>
    private static GatewaySettings Settings(int requestsPerMinute = 60) => new() {
        ConnectionString = Environment.GetEnvironmentVariable("ANGI_GATEWAY_TEST_CONNECTION")!,
        MaxInFlight = 10, RequestsPerMinute = requestsPerMinute, PricingValidUntil = new(2099, 1, 1) };

    /// <summary>Recreates ai_control in the test database with zero usage, then optionally opens spending with fixed budgets.</summary>
    private static async Task<(GatewaySettings Settings, PgBudgetLedger Ledger)> ResetAsync(bool enabled = true, int requestsPerMinute = 60)
    {
        var settings = Settings(requestsPerMinute);
        var name = new NpgsqlConnectionStringBuilder(settings.ConnectionString).Database;
        if (name is null || !name.EndsWith("_test", StringComparison.Ordinal)) throw new InvalidOperationException("An isolated *_test database is required.");
        var ledger = new PgBudgetLedger(settings);
        await ledger.InitializeAsync(default);
        await SqlAsync(settings, "TRUNCATE ai_control.reservations; UPDATE ai_control.control SET stopped=true,pricing_confirmed=false; UPDATE ai_control.budgets SET daily_usd=0,monthly_usd=0,daily_calls=0,monthly_calls=0;");
        if (enabled)
        {
            foreach (var scope in new[] { "global", "chat" })
                await ledger.ChangeBudgetAsync(scope, new(10, 20, 50, 100), default);
            await ledger.ChangeControlAsync(new(false, GatewaySettings.PriceVersion), default);
        }
        return (settings, ledger);
    }

    // TEST-01: A new ledger starts stopped with zero budgets, so opening it still denies spending until budgets are set.
    /// <summary>Proves the safe defaults survive restarts and new instances.</summary>
    [GatewayPostgresFact]
    public async Task DefaultZeroBudgetAndStoppedStateSurviveNewInstances()
    {
        var (settings, ledger) = await ResetAsync(false);
        var error = await Assert.ThrowsAsync<GatewayDenied>(() => new PgBudgetLedger(settings).ReserveAsync(new RequestPolicy(settings).Prepare("chat", RequestPolicyTests.Body()), default));
        Assert.Equal("gateway_spending_stopped", error.Code);
        await ledger.ChangeControlAsync(new(false, GatewaySettings.PriceVersion), default);
        error = await Assert.ThrowsAsync<GatewayDenied>(() => new PgBudgetLedger(settings).ReserveAsync(new RequestPolicy(settings).Prepare("chat", RequestPolicyTests.Body()), default));
        Assert.Equal("gateway_budget_exhausted", error.Code);
    }

    // TEST-02: Twenty-four concurrent admissions against a budget for one call admit exactly one.
    /// <summary>Checks the control-row lock prevents overspending across connections.</summary>
    [GatewayPostgresFact]
    public async Task IndependentConnectionsRaceForOneBudgetWithoutOverspending()
    {
        var (settings, ledger) = await ResetAsync();
        var request = new RequestPolicy(settings).Prepare("chat", RequestPolicyTests.Body());
        await ledger.ChangeBudgetAsync("global", new(request.ReservedUsd, request.ReservedUsd, 50, 100), default);
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(async _ => {
            try { return await new PgBudgetLedger(settings).ReserveAsync(request, default); }
            catch (GatewayDenied) { return null; }
        }));
        Assert.Single(results, result => result is not null);
        var state = await new PgBudgetLedger(settings).StatusAsync(default);
        Assert.Equal(1, state.GetProperty("reservations").GetArrayLength());
    }

    // TEST-03: A pending reservation left by a crash blocks concurrency with a short Retry-After hint and can be closed only while stopped, at full charge.
    /// <summary>Ensures crashes never refund holds or free concurrency slots automatically.</summary>
    [GatewayPostgresFact]
    public async Task PendingCrashReservationBlocksConcurrencyAfterRestart()
    {
        var (settings, _) = await ResetAsync();
        settings.MaxInFlight = 1;
        var ledger = new PgBudgetLedger(settings);
        await ledger.ChangeControlAsync(new(true, null), default);
        await ledger.ChangeControlAsync(new(false, GatewaySettings.PriceVersion), default);
        var request = new RequestPolicy(settings).Prepare("chat", RequestPolicyTests.Body());
        var reservation = await ledger.ReserveAsync(request, default);
        var error = await Assert.ThrowsAsync<GatewayDenied>(() => new PgBudgetLedger(settings).ReserveAsync(request, default));
        Assert.Equal("gateway_concurrency_limit", error.Code);
        Assert.Equal(429, error.Status); Assert.Equal(PgBudgetLedger.ConcurrencyRetrySeconds, error.RetryAfterSeconds);
        await Assert.ThrowsAsync<GatewayDenied>(() => ledger.SettleAsync(reservation, null, "abandoned", true, default));
        await ledger.ChangeControlAsync(new(true, null), default);
        await ledger.SettleAsync(reservation, null, "abandoned", true, default);
        var state = await new PgBudgetLedger(settings).StatusAsync(default);
        Assert.True(state.GetProperty("control").GetProperty("stopped").GetBoolean());
        Assert.Equal(reservation.ReservedUsd, state.GetProperty("reservations")[0].GetProperty("charged_upper_usd").GetDecimal());
    }

    // TEST-04: Re-saving the same budget does not reset the global daily call count.
    /// <summary>Prevents budget edits from erasing usage already admitted.</summary>
    [GatewayPostgresFact]
    public async Task GlobalCallCapCannotBeResetByBudgetEdit()
    {
        var (settings, ledger) = await ResetAsync();
        await ledger.ChangeBudgetAsync("global", new(10, 20, 1, 1), default);
        var reservation = await ledger.ReserveAsync(new RequestPolicy(settings).Prepare("chat", RequestPolicyTests.Body()), default);
        await ledger.SettleAsync(reservation, Usage(), "success", false, default);
        await ledger.ChangeBudgetAsync("global", new(10, 20, 1, 1), default);
        var error = await Assert.ThrowsAsync<GatewayDenied>(() => ledger.ReserveAsync(new RequestPolicy(settings).Prepare("chat", RequestPolicyTests.Body()), default));
        Assert.Equal("gateway_budget_exhausted", error.Code);
    }

    // TEST-05: The removed vector workload is rejected for reservations and budgets, and status lists only chat and global.
    /// <summary>Keeps the gateway limited to backend chat spending.</summary>
    [GatewayPostgresFact]
    public async Task LedgerRejectsRemovedWorkloadAndOnlyExposesBackendBudgets()
    {
        var (settings, ledger) = await ResetAsync();
        var request = new RequestPolicy(settings).Prepare("chat", RequestPolicyTests.Body());
        var error = await Assert.ThrowsAsync<GatewayDenied>(() => ledger.ReserveAsync(request with { Workload = "vectors" }, default));
        Assert.Equal("gateway_invalid_reservation", error.Code);
        error = await Assert.ThrowsAsync<GatewayDenied>(() => ledger.ChangeBudgetAsync("vectors", new(1, 1, 1, 1), default));
        Assert.Equal("gateway_invalid_budget", error.Code);
        var state = await ledger.StatusAsync(default);
        Assert.Equal(new[] { "chat", "global" }, state.GetProperty("budgets").EnumerateArray()
            .Select(budget => budget.GetProperty("workload").GetString()).Order().ToArray());
        Assert.False(state.TryGetProperty("batches", out _));
        Assert.Empty(state.GetProperty("reservations").EnumerateArray());
    }

    // TEST-06: An unknown outcome keeps the full hold even if a later duplicate settlement reports success.
    /// <summary>Guarantees uncertain costs cannot be refunded by replayed settlements.</summary>
    [GatewayPostgresFact]
    public async Task UnknownOutcomeIsFullyChargedAndNeverRefundedByDuplicateSettlement()
    {
        var (settings, ledger) = await ResetAsync();
        var request = new RequestPolicy(settings).Prepare("chat", RequestPolicyTests.Body());
        await ledger.ChangeBudgetAsync("chat", new(request.ReservedUsd, request.ReservedUsd, 10, 10), default);
        var reservation = await ledger.ReserveAsync(request, default);
        await ledger.SettleAsync(reservation, null, "unknown", true, default);
        await ledger.SettleAsync(reservation, Usage(), "success", false, default);
        await ledger.ChangeControlAsync(new(false, GatewaySettings.PriceVersion), default);
        await Assert.ThrowsAsync<GatewayDenied>(() => new PgBudgetLedger(settings).ReserveAsync(request, default));
        var state = await ledger.StatusAsync(default);
        Assert.Equal(reservation.ReservedUsd, state.GetProperty("reservations")[0].GetProperty("charged_upper_usd").GetDecimal());
    }

    // TEST-07: The monthly call limit counts admissions from earlier days of the month.
    /// <summary>Checks monthly caps are not reset by the day boundary.</summary>
    [GatewayPostgresFact]
    public async Task MonthlyCallLimitIncludesPreviousDays()
    {
        var (settings, ledger) = await ResetAsync();
        await ledger.ChangeBudgetAsync("chat", new(10, 20, 1, 1), default);
        var reservation = await ledger.ReserveAsync(new RequestPolicy(settings).Prepare("chat", RequestPolicyTests.Body()), default);
        await ledger.SettleAsync(reservation, Usage(), "success", false, default);
        await SqlAsync(settings, "UPDATE ai_control.reservations SET day_key=day_key-1");
        await Assert.ThrowsAsync<GatewayDenied>(() => ledger.ReserveAsync(new RequestPolicy(settings).Prepare("chat", RequestPolicyTests.Body()), default));
        await ledger.ChangeBudgetAsync("chat", new(10, 20, 1, 2), default);
        await ledger.ReserveAsync(new RequestPolicy(settings).Prepare("chat", RequestPolicyTests.Body()), default);
    }

    // TEST-08: An instance whose limits differ from the confirmed policy cannot admit requests.
    /// <summary>Prevents replicas with different limits from sharing one ledger silently.</summary>
    [GatewayPostgresFact]
    public async Task MixedPolicyVersionsCannotAdmitRequests()
    {
        var (settings, _) = await ResetAsync();
        settings.ChatMaxOutputTokens = 1024;
        var error = await Assert.ThrowsAsync<GatewayDenied>(() => new PgBudgetLedger(settings).ReserveAsync(new RequestPolicy(settings).Prepare("chat", RequestPolicyTests.Body()), default));
        Assert.Equal("gateway_policy_mismatch", error.Code);
    }

    // TEST-09: Spending cannot be resumed with an expired pricing review.
    /// <summary>Makes expired pricing fail closed.</summary>
    [GatewayPostgresFact]
    public async Task ExpiredPricingCannotBeActivated()
    {
        var (settings, ledger) = await ResetAsync();
        await ledger.ChangeControlAsync(new(true, null), default);
        settings.PricingValidUntil = new(2020, 1, 1);
        await Assert.ThrowsAsync<GatewayDenied>(() => new PgBudgetLedger(settings).ChangeControlAsync(new(false, GatewaySettings.PriceVersion), default));
    }

    // TEST-10: A request over the per-minute limit is denied with 429 gateway_rate_limit and a Retry-After hint within the one-minute window.
    /// <summary>Lets callers wait for the next free slot instead of treating the limit as a spending stop.</summary>
    [GatewayPostgresFact]
    public async Task RateLimitDenialCarriesRetryAfterWithinWindow()
    {
        var (settings, ledger) = await ResetAsync(requestsPerMinute: 1);
        var reservation = await ledger.ReserveAsync(new RequestPolicy(settings).Prepare("chat", RequestPolicyTests.Body()), default);
        await ledger.SettleAsync(reservation, Usage(), "success", false, default);

        var error = await Assert.ThrowsAsync<GatewayDenied>(() => ledger.ReserveAsync(new RequestPolicy(settings).Prepare("chat", RequestPolicyTests.Body()), default));

        Assert.Equal("gateway_rate_limit", error.Code); Assert.Equal(429, error.Status);
        Assert.InRange(error.RetryAfterSeconds!.Value, 1, 60);
    }

    // TEST-11: The retry hint is the whole seconds until the oldest admission leaves the window, clamped to 1..60; covers 10 s left,
    // a fraction of a second left, a just-admitted request and an admission already outside the window.
    /// <summary>Checks the hint never asks for zero or more than a minute of waiting.</summary>
    [Theory]
    [InlineData(50, 10)]
    [InlineData(59.2, 1)]
    [InlineData(0, 60)]
    [InlineData(61, 1)]
    public void RateRetrySeconds_IsClampedWholeSeconds(double ageSeconds, int expected)
    {
        var now = new DateTime(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc);

        Assert.Equal(expected, PgBudgetLedger.RateRetrySeconds(now.AddSeconds(-ageSeconds), now));
    }

    /// <summary>Returns measured usage for a small successful call.</summary>
    private static CompletionUsage Usage() => new(100, 50, 0, RequestPolicy.Cost(100, 50));
    /// <summary>Runs one SQL statement against the isolated test database.</summary>
    private static async Task SqlAsync(GatewaySettings settings, string sql)
    {
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
