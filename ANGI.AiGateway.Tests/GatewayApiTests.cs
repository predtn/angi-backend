using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ANGI.AiGateway.Tests;

public sealed class GatewayApiTests
{
    private const string ChatKey = "angi-chat-cccccccccccccccccccccccccccccccccccccccc";
    private const string AdminKey = "angi-admin-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task ScopedCredentialsQuoteWithoutSpendingAndCannotManageBudgets()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", ChatKey);
        using var quote = await client.PostAsync("/v1/quote", Content());
        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
        Assert.Contains("chat", await quote.Content.ReadAsStringAsync());
        Assert.Contains("gpt-6-luna", await quote.Content.ReadAsStringAsync());
        using var admin = await client.GetAsync("/admin/status");
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", AdminKey);
        using var blocked = await client.PostAsync("/v1/chat/completions", Content());
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
    }

    [Fact]
    public async Task LegacyVectorCredentialCannotCallAiEvenWhenOldSettingRemains()
    {
        const string oldKey = "angi-vectors-vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv";
        using var factory = Factory().WithWebHostBuilder(builder => builder.UseSetting("Gateway:VectorKey", oldKey));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", oldKey);
        foreach (var path in new[] { "/v1/quote", "/v1/chat/completions" })
        {
            using var denied = await client.PostAsync(path, Content());
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
    }

    [Fact]
    public async Task VectorBudgetAndBatchManagementAreUnavailable()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", AdminKey);
        using var budget = await client.PutAsync("/admin/budgets/vectors",
            new StringContent("{\"dailyUsd\":1,\"monthlyUsd\":1,\"dailyCalls\":1,\"monthlyCalls\":1}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, budget.StatusCode);
        Assert.Contains("gateway_invalid_budget", await budget.Content.ReadAsStringAsync());
        foreach (var path in new[] { "/admin/batches", $"/admin/batches/{Guid.NewGuid()}/review" })
        {
            using var removed = await client.PostAsync(path, Content());
            Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        }
    }

    [Fact]
    public async Task AllBillableRoutesRequireAuthAndOnlyChatCompletionsExists()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        using var denied = await client.PostAsync("/v1/chat/completions", Content());
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", ChatKey);
        using var other = await client.PostAsync("/v1/responses", Content());
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task LedgerFailureAtRealHttpEndpointReturns503BeforeProvider()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", ChatKey);
        using var response = await client.PostAsync("/v1/chat/completions", Content());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("gateway_ledger_unavailable", await response.Content.ReadAsStringAsync());
    }

    private static ByteArrayContent Content()
    {
        var content = new ByteArrayContent(RequestPolicyTests.Body());
        content.Headers.ContentType = new("application/json"); return content;
    }
    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
        builder.UseSetting("Gateway:ChatKey", ChatKey)
            .UseSetting("Gateway:AdminKey", AdminKey).UseSetting("Gateway:ProviderApiKey", "fake-provider-test-key")
            .UseSetting("Gateway:ConnectionString", "");
        builder.ConfigureTestServices(services => services.AddHttpClient<GatewayService>()
            .ConfigurePrimaryHttpMessageHandler(() => new NeverNetwork()));
    });
    private sealed class NeverNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Tests must never reach an upstream provider.");
    }
}
