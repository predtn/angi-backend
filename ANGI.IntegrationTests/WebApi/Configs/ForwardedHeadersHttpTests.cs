using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ANGI.IntegrationTests.WebApi.Configs;

/// <summary>Checks which client IP the anonymous rate limit counts, with and without ForwardedHeaders:Enabled.</summary>
public sealed class ForwardedHeadersHttpTests
{
    private const string AnonymousEndpoint = "/api/v1/tags";

    // TEST-01: Behind the proxy, two clients get their own anonymous budget instead of sharing the proxy's.
    /// <summary>Verifies X-Forwarded-For becomes the rate-limit IP when the setting is on.</summary>
    [Fact]
    public async Task Enabled_ShouldLimitEachForwardedClientSeparately()
    {
        await using var factory = new ForwardedHeadersApiFactory(enabled: true);
        using var client = factory.CreateClient();

        var first = await StatusesAsync(client, "203.0.113.1", 3);
        var second = await StatusesAsync(client, "203.0.113.2", 1);

        first.Take(2).Should().NotContain(HttpStatusCode.TooManyRequests);
        first[2].Should().Be(HttpStatusCode.TooManyRequests);
        second[0].Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    // TEST-02: Without the setting the header is ignored, so a client cannot dodge the limit by inventing IPs.
    /// <summary>Verifies the default keeps the connection IP.</summary>
    [Fact]
    public async Task Disabled_ShouldIgnoreForwardedForHeader()
    {
        await using var factory = new ForwardedHeadersApiFactory(enabled: false);
        using var client = factory.CreateClient();

        var first = await StatusesAsync(client, "203.0.113.1", 2);
        var spoofed = await StatusesAsync(client, "203.0.113.99", 1);

        first.Should().NotContain(HttpStatusCode.TooManyRequests);
        spoofed[0].Should().Be(HttpStatusCode.TooManyRequests);
    }

    private static async Task<HttpStatusCode[]> StatusesAsync(HttpClient client, string forwardedFor, int count)
    {
        var statuses = new HttpStatusCode[count];
        for (var index = 0; index < count; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AnonymousEndpoint);
            request.Headers.Add("X-Forwarded-For", forwardedFor);
            using var response = await client.SendAsync(request);
            statuses[index] = response.StatusCode;
        }

        return statuses;
    }

    /// <summary>A host whose anonymous limit is 2 requests a minute; the database is unreachable, which these checks ignore.</summary>
    private sealed class ForwardedHeadersApiFactory(bool enabled) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Program reads these while registering services, before in-memory overrides apply.
            var values = new Dictionary<string, string>
            {
                ["Jwt__Issuer"] = "ANGI.ForwardedHeaders.Tests",
                ["Jwt__Audience"] = "ANGI.ForwardedHeaders.Tests.Client",
                ["Jwt__SecretKey"] = "ANGI-forwarded-headers-test-secret-key-1234567890",
                ["ConnectionStrings__Default"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused",
                ["RateLimit__GeneralPermitLimit"] = "2",
                ["RateLimit__GeneralWindowSeconds"] = "60",
                ["ForwardedHeaders__Enabled"] = enabled ? "true" : "false"
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

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureLogging(logging => logging.ClearProviders());
        }
    }
}
