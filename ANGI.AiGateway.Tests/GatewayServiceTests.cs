using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace ANGI.AiGateway.Tests;

/// <summary>Verifies one admission per upstream attempt, conservative settlement and the error envelope the gateway returns.</summary>
public sealed class GatewayServiceTests
{
    private const string Success = "{\"choices\":[{\"message\":{\"content\":\"{}\"}}],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":50,\"total_tokens\":150}}";

    // TEST-01: An unavailable ledger or a denied budget makes no paid provider attempt; covers a budget denial and a database failure.
    /// <summary>Ensures money is never spent when the admission cannot be recorded or is refused.</summary>
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

    // TEST-02: A successful attempt is admitted before the call and settles only the measured usage without stopping spending.
    /// <summary>Checks the reservation is released down to the conservative charge of the reported tokens.</summary>
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

    // TEST-03: An uncertain or rejected upstream outcome stops spending, keeps the full hold and hides provider details; covers missing usage,
    // an upstream 429 and an upstream 500.
    /// <summary>Guarantees an unknown cost is charged in full and never retried silently.</summary>
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

    // TEST-04: A timeout keeps the reservation and settles with a token that is not the cancelled request token.
    /// <summary>Ensures settlement still completes after the caller's request was cancelled.</summary>
    [Fact]
    public async Task TimeoutRetainsReservationAndUsesIndependentSettlementToken()
    {
        var ledger = new FakeLedger(); var provider = new FakeProvider(() => throw new TaskCanceledException());
        await Service(ledger, provider).ExecuteAsync("chat", RequestPolicyTests.Body(), default);
        Assert.True(ledger.Stopped); Assert.Null(ledger.Usage); Assert.Equal(1, provider.Calls);
        Assert.False(ledger.AccountingTokenCancelled);
    }

    // TEST-05: A failed settlement reports gateway_settlement_unavailable instead of an unaccounted success, without retrying upstream.
    /// <summary>Prevents a paid response from being returned when its cost could not be recorded.</summary>
    [Fact]
    public async Task FailedSettlementDoesNotReturnUnaccountedSuccessOrRetry()
    {
        var ledger = new FakeLedger { SettlementError = new IOException("db down") };
        var provider = new FakeProvider(() => new(HttpStatusCode.OK) { Content = new StringContent(Success) });
        var error = await Assert.ThrowsAsync<GatewayDenied>(() => Service(ledger, provider).ExecuteAsync("chat", RequestPolicyTests.Body(), default));
        Assert.Equal("gateway_settlement_unavailable", error.Code); Assert.Equal(1, provider.Calls);
    }

    // TEST-06: Every caller retry is a new admission, so a denied retry makes no second upstream call.
    /// <summary>Checks retries cannot bypass the budget by reusing an earlier admission.</summary>
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

    // TEST-07: Temporary rate and concurrency denials are written as 429 with the code and a Retry-After header; covers gateway_rate_limit
    // and gateway_concurrency_limit.
    /// <summary>Lets the backend wait and retry a temporary gateway limit instead of reporting a spending stop.</summary>
    [Theory]
    [InlineData("gateway_rate_limit", 17)]
    [InlineData("gateway_concurrency_limit", 3)]
    public async Task WriteError_TemporaryLimit_AddsRetryAfter(string code, int retryAfter)
    {
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();

        await GatewayService.WriteErrorAsync(context.Response, new GatewayDenied(code, 429, retryAfter));

        Assert.Equal(429, context.Response.StatusCode);
        Assert.Equal(retryAfter.ToString(), context.Response.Headers.RetryAfter.ToString());
        Assert.Contains(code, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    // TEST-08: Spending stops, exhausted budgets and unexpected failures never carry Retry-After; covers a stop, a budget denial and a ledger failure.
    /// <summary>Keeps non-temporary denials from inviting automatic retries.</summary>
    [Theory]
    [InlineData("stopped")][InlineData("budget")][InlineData("ledger")]
    public async Task WriteError_NonTemporaryFailure_HasNoRetryAfter(string area)
    {
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        Exception error = area switch
        {
            "stopped" => new GatewayDenied("gateway_spending_stopped"),
            "budget" => new GatewayDenied("gateway_budget_exhausted", 429),
            _ => new IOException("database unavailable")
        };

        await GatewayService.WriteErrorAsync(context.Response, error);

        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));
        Assert.Equal(area == "budget" ? 429 : 503, context.Response.StatusCode);
    }

    /// <summary>Builds a gateway service around fake ledger and provider handlers with a key that is never sent to a real network.</summary>
    private static GatewayService Service(FakeLedger ledger, FakeProvider provider)
    {
        var settings = new GatewaySettings { ProviderApiKey = "fake-test-key-never-sent-to-network" };
        return new(settings, new(settings), ledger, new(provider));
    }

    /// <summary>Counts upstream calls and returns a scripted provider response for the OpenAI chat endpoint.</summary>
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

    /// <summary>Records admissions and settlements in memory, and can fail either step on demand.</summary>
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
