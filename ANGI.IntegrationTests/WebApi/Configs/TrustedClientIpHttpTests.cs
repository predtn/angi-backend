using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ANGI.IntegrationTests.WebApi.Configs;

/// <summary>
/// Checks that the anonymous rate limit counts the browser IP sent by the Next.js server only when the shared proxy key
/// matches, and that /health answers without the database.
/// </summary>
public sealed class TrustedClientIpHttpTests
{
    private const string AnonymousEndpoint = "/api/v1/tags";
    private const string ProxyKey = "ANGI-trusted-client-ip-test-key-1234567890";

    // TEST-01: With the right key, two visitors behind the same frontend server get their own budget.
    /// <summary>Verifies X-Angi-Client-Ip becomes the rate-limit IP when X-Angi-Proxy-Key matches.</summary>
    [Fact]
    public async Task MatchingKey_ShouldLimitEachClientSeparately()
    {
        await using var factory = new TrustedClientIpApiFactory(ProxyKey);
        using var client = factory.CreateClient();

        var first = await StatusesAsync(client, ProxyKey, "198.51.100.1", 3);
        var second = await StatusesAsync(client, ProxyKey, "198.51.100.2", 1);

        first.Take(2).Should().NotContain(HttpStatusCode.TooManyRequests);
        first[2].Should().Be(HttpStatusCode.TooManyRequests);
        second[0].Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    // TEST-02: A wrong key is ignored, so a direct caller cannot pick a new IP to dodge the limit.
    /// <summary>Verifies the connection IP stays when the key does not match.</summary>
    [Fact]
    public async Task WrongKey_ShouldIgnoreClientIpHeader()
    {
        await using var factory = new TrustedClientIpApiFactory(ProxyKey);
        using var client = factory.CreateClient();

        var first = await StatusesAsync(client, "wrong-key", "198.51.100.1", 2);
        var spoofed = await StatusesAsync(client, "wrong-key", "198.51.100.99", 1);

        first.Should().NotContain(HttpStatusCode.TooManyRequests);
        spoofed[0].Should().Be(HttpStatusCode.TooManyRequests);
    }

    // TEST-03: Without a configured key (local, tests) the headers are never trusted, even with an empty key sent.
    /// <summary>Verifies an unset ProxyKey turns the feature off.</summary>
    [Fact]
    public async Task NoConfiguredKey_ShouldIgnoreClientIpHeader()
    {
        await using var factory = new TrustedClientIpApiFactory(proxyKey: "");
        using var client = factory.CreateClient();

        var first = await StatusesAsync(client, "", "198.51.100.1", 2);
        var spoofed = await StatusesAsync(client, "", "198.51.100.99", 1);

        first.Should().NotContain(HttpStatusCode.TooManyRequests);
        spoofed[0].Should().Be(HttpStatusCode.TooManyRequests);
    }

    // TEST-04: /health reports an unreachable database as 503 SERVICE_UNAVAILABLE in ApiResponse, and is not rate limited.
    /// <summary>Verifies the health endpoint shape and that it stays outside the API limits.</summary>
    [Fact]
    public async Task Health_WithUnreachableDatabase_ShouldReturn503StatusOnly()
    {
        await using var factory = new TrustedClientIpApiFactory(ProxyKey);
        using var client = factory.CreateClient();

        for (var index = 0; index < 3; index++)
        {
            using var response = await client.GetAsync("/health");
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("\"success\":false").And.Contain("\"errorCode\":\"SERVICE_UNAVAILABLE\"");
        }
    }

    /// <summary>Sends anonymous requests with the proxy key and client IP headers and returns their statuses.</summary>
    private static async Task<HttpStatusCode[]> StatusesAsync(HttpClient client, string key, string clientIp, int count)
    {
        var statuses = new HttpStatusCode[count];
        for (var index = 0; index < count; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AnonymousEndpoint);
            request.Headers.Add("X-Angi-Proxy-Key", key);
            request.Headers.Add("X-Angi-Client-Ip", clientIp);
            using var response = await client.SendAsync(request);
            statuses[index] = response.StatusCode;
        }

        return statuses;
    }

    /// <summary>A host whose anonymous limit is 2 requests a minute; the database is unreachable.</summary>
    private sealed class TrustedClientIpApiFactory(string proxyKey) : WebApplicationFactory<Program>
    {
        /// <summary>Sets the environment variables Program reads during service registration, then builds the host.</summary>
        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Program reads these while registering services, before in-memory overrides apply.
            var values = new Dictionary<string, string?>
            {
                ["Jwt__Issuer"] = "ANGI.TrustedClientIp.Tests",
                ["Jwt__Audience"] = "ANGI.TrustedClientIp.Tests.Client",
                ["Jwt__SecretKey"] = "ANGI-trusted-client-ip-test-secret-key-1234567890",
                ["ConnectionStrings__Default"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1",
                ["RateLimit__GeneralPermitLimit"] = "2",
                ["RateLimit__GeneralWindowSeconds"] = "60",
                ["TrustedClientIp__ProxyKey"] = proxyKey.Length == 0 ? null : proxyKey
            };
            var previous = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            foreach (var (name, value) in values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            try
            {
                return base.CreateHost(builder);
            }
            finally
            {
                foreach (var (name, value) in previous)
                {
                    Environment.SetEnvironmentVariable(name, value);
                }
            }
        }

        /// <summary>Runs the host as Production without log output.</summary>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureLogging(logging => logging.ClearProviders());
        }
    }
}
