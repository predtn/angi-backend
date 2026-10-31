using ANGI.AiGateway.Storage;
using Npgsql;
using Xunit;

namespace ANGI.AiGateway.Tests;

public sealed class GatewayPostgresFactAttribute : FactAttribute
{
    public GatewayPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANGI_GATEWAY_TEST_CONNECTION")))
            Skip = "Set ANGI_GATEWAY_TEST_CONNECTION to an isolated *_test PostgreSQL database.";
    }
}

// The whole class runs sequentially. Each test touches only ai_control inside an explicitly named test database.
public sealed class PgBudgetLedgerTests
{
    private static GatewaySettings Settings() => new() {
        ConnectionString = Environment.GetEnvironmentVariable("ANGI_GATEWAY_TEST_CONNECTION")!,
        MaxInFlight = 10, RequestsPerMinute = 60, PricingValidUntil = new(2099, 1, 1) };

    private static async Task<(GatewaySettings Settings, PgBudgetLedger Ledger)> ResetAsync(bool enabled = true)
    {
        var settings = Settings();
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
        await Assert.ThrowsAsync<GatewayDenied>(() => ledger.SettleAsync(reservation, null, "abandoned", true, default));
        await ledger.ChangeControlAsync(new(true, null), default);
        await ledger.SettleAsync(reservation, null, "abandoned", true, default);
        var state = await new PgBudgetLedger(settings).StatusAsync(default);
        Assert.True(state.GetProperty("control").GetProperty("stopped").GetBoolean());
        Assert.Equal(reservation.ReservedUsd, state.GetProperty("reservations")[0].GetProperty("charged_upper_usd").GetDecimal());
    }

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

    [GatewayPostgresFact]
    public async Task MixedPolicyVersionsCannotAdmitRequests()
    {
        var (settings, _) = await ResetAsync();
        settings.ChatMaxOutputTokens = 1024;
        var error = await Assert.ThrowsAsync<GatewayDenied>(() => new PgBudgetLedger(settings).ReserveAsync(new RequestPolicy(settings).Prepare("chat", RequestPolicyTests.Body()), default));
        Assert.Equal("gateway_policy_mismatch", error.Code);
    }

    [GatewayPostgresFact]
    public async Task ExpiredPricingCannotBeActivated()
    {
        var (settings, ledger) = await ResetAsync();
        await ledger.ChangeControlAsync(new(true, null), default);
        settings.PricingValidUntil = new(2020, 1, 1);
        await Assert.ThrowsAsync<GatewayDenied>(() => new PgBudgetLedger(settings).ChangeControlAsync(new(false, GatewaySettings.PriceVersion), default));
    }

    private static CompletionUsage Usage() => new(100, 50, 0, RequestPolicy.Cost(100, 50));
    private static async Task SqlAsync(GatewaySettings settings, string sql)
    {
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
