using System.Net;
using System.Text;
using Xunit;

namespace ANGI.AiGateway.Tests;

public sealed class GatewayServiceTests
{
    private const string Success = "{\"choices\":[{\"message\":{\"content\":\"{}\"}}],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":50,\"total_tokens\":150}}";

    [Fact]
    public async Task LedgerUnavailableOrBudgetDeniedMakesZeroPaidAttempts()
    {
        foreach (var failure in new Exception[] { new GatewayDenied("gateway_budget_exhausted"), new IOException("database unavailable") })
        {
            var ledger = new FakeLedger { ReserveError = failure };
            var provider = new FakeProvider();
            await Assert.ThrowsAnyAsync<Exception>(() => Service(ledger, provider).ExecuteAsync("chat", RequestPolicyTests.Body(), default));
            Assert.Equal(0, provider.Calls);
        }
    }

    [Fact]
    public async Task SuccessSettlesOnlyMeasuredUsageAfterAdmission()
    {
        var ledger = new FakeLedger(); var provider = new FakeProvider(() => {
            Assert.Equal(1, ledger.Reservations); return new(HttpStatusCode.OK) { Content = new StringContent(Success) }; });
        var response = await Service(ledger, provider).ExecuteAsync("chat", RequestPolicyTests.Body(), default);
        Assert.Equal(200, response.Status);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(RequestPolicy.Cost(100, 50), ledger.Usage!.ChargedUpperUsd);
        Assert.False(ledger.Stopped);
    }

    [Theory]
    [InlineData(200, "{}", "unknown")]
    [InlineData(429, "private provider error", "upstream_error")]
    [InlineData(500, "secret error", "upstream_error")]
    public async Task UncertainOrRejectedAttemptStopsAndRetainsFullReservation(int status, string body, string outcome)
    {
        var ledger = new FakeLedger(); var provider = new FakeProvider(() => new((HttpStatusCode)status) { Content = new StringContent(body) });
        var response = await Service(ledger, provider).ExecuteAsync("chat", RequestPolicyTests.Body(), default);
        Assert.Equal(503, response.Status);
        Assert.Null(ledger.Usage); Assert.True(ledger.Stopped); Assert.Equal(outcome, ledger.Outcome);
        Assert.Equal(1, provider.Calls); // No hidden SDK/transport retry.
        Assert.DoesNotContain("private", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public async Task TimeoutRetainsReservationAndUsesIndependentSettlementToken()
    {
        var ledger = new FakeLedger(); var provider = new FakeProvider(() => throw new TaskCanceledException());
        await Service(ledger, provider).ExecuteAsync("chat", RequestPolicyTests.Body(), default);
        Assert.True(ledger.Stopped); Assert.Null(ledger.Usage); Assert.Equal(1, provider.Calls);
        Assert.False(ledger.AccountingTokenCancelled);
    }

    [Fact]
    public async Task FailedSettlementDoesNotReturnUnaccountedSuccessOrRetry()
    {
        var ledger = new FakeLedger { SettlementError = new IOException("db down") };
        var provider = new FakeProvider(() => new(HttpStatusCode.OK) { Content = new StringContent(Success) });
        var error = await Assert.ThrowsAsync<GatewayDenied>(() => Service(ledger, provider).ExecuteAsync("chat", RequestPolicyTests.Body(), default));
        Assert.Equal("gateway_settlement_unavailable", error.Code); Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task EveryCallerRetryHasAnIndependentAdmission()
    {
        var ledger = new FakeLedger(); var provider = new FakeProvider(() => new(HttpStatusCode.OK) { Content = new StringContent(Success) });
        var service = Service(ledger, provider);
        await service.ExecuteAsync("chat", RequestPolicyTests.Body(), default);
        ledger.ReserveError = new GatewayDenied("gateway_budget_exhausted");
        await Assert.ThrowsAsync<GatewayDenied>(() => service.ExecuteAsync("chat", RequestPolicyTests.Body(), default));
        Assert.Equal(1, provider.Calls); Assert.Equal(2, ledger.Reservations);
    }

    private static GatewayService Service(FakeLedger ledger, FakeProvider provider)
    {
        var settings = new GatewaySettings { ProviderApiKey = "fake-test-key-never-sent-to-network" };
        return new(settings, new(settings), ledger, new(provider));
    }

    private sealed class FakeProvider(Func<HttpResponseMessage>? response = null) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.ToString());
            return Task.FromResult(response?.Invoke() ?? new(HttpStatusCode.OK) { Content = new StringContent(Success) });
        }
    }

    private sealed class FakeLedger : IBudgetLedger
    {
        public Exception? ReserveError, SettlementError;
        public int Reservations;
        public CompletionUsage? Usage;
        public string? Outcome;
        public bool Stopped, AccountingTokenCancelled;
        public Task<Reservation> ReserveAsync(PreparedRequest request, CancellationToken ct)
        {
            Reservations++;
            if (ReserveError is not null) throw ReserveError;
            return Task.FromResult(new Reservation(Guid.NewGuid(), request.Workload, request.ReservedUsd));
        }
        public Task SettleAsync(Reservation reservation, CompletionUsage? usage, string outcome, bool stop, CancellationToken ct)
        {
            if (SettlementError is not null) throw SettlementError;
            Usage = usage; Outcome = outcome; Stopped = stop; AccountingTokenCancelled = ct.IsCancellationRequested;
            return Task.CompletedTask;
        }
    }
}
