using System.Net;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Infrastructure.Services.Mapbox;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.IntegrationTests.Infrastructure.Services.Mapbox;

public sealed class MapboxHttpIntegrationTests
{
    // TEST-01: Resolve the real typed Mapbox client, call an HTTP server, map its response, and cache the route.
    /// <summary>Verifies the ANGI-230 outbound HTTP and dependency-injection boundary without using a live Mapbox token.</summary>
    [Fact]
    public async Task CalculateRouteAsync_ThroughRealHttpTransport_ShouldMapAndCacheMapboxResponse()
    {
        var requestCount = 0;
        var receivedPathAndQuery = string.Empty;
        var mapboxStub = await StartMapboxStubAsync(async context =>
        {
            requestCount++;
            receivedPathAndQuery = $"{context.Request.Path}{context.Request.QueryString}";
            await Results.Json(new
            {
                routes = new[]
                {
                    new
                    {
                        legs = new[]
                        {
                            new { distance = 2350.4, duration = 540.5 },
                            new { distance = 3100.6, duration = 660.4 }
                        }
                    }
                }
            }).ExecuteAsync(context);
        });

        await using (mapboxStub.Server)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mapbox:BaseUrl"] = mapboxStub.BaseUrl,
                ["Mapbox:AccessToken"] = "integration token",
                ["Mapbox:TimeoutMilliseconds"] = "3000",
                ["Mapbox:CacheHours"] = "1"
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMapboxInfrastructure(configuration);
            await using var provider = services.BuildServiceProvider();
            var routeService = provider.GetRequiredService<IRouteService>();
            var destinations = new[]
            {
                new RouteCoordinate(16.067m, 108.220m),
                new RouteCoordinate(16.074m, 108.215m)
            };

            var first = await routeService.CalculateRouteAsync(
                new RouteCoordinate(16.061204m, 108.227531m),
                destinations,
                RouteProfile.Driving,
                CancellationToken.None);
            var cached = await routeService.CalculateRouteAsync(
                new RouteCoordinate(16.061204m, 108.227531m),
                destinations,
                RouteProfile.Driving,
                CancellationToken.None);

            first.IsEstimated.Should().BeFalse();
            first.Legs.Should().Equal(new RouteMetric(2350, 541), new RouteMetric(3101, 660));
            cached.Should().BeEquivalentTo(first);
            requestCount.Should().Be(1);
            receivedPathAndQuery.Should().Be(
                "/directions/v5/mapbox/driving/108.227531,16.061204;108.22,16.067;108.215,16.074" +
                "?access_token=integration%20token&overview=false&steps=false");
        }
    }

    /// <summary>Starts a loopback HTTP server that emulates the Mapbox Directions endpoint.</summary>
    private static async Task<MapboxStub> StartMapboxStubAsync(RequestDelegate handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var application = builder.Build();
        application.Run(handler);
        await application.StartAsync();

        var addresses = application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses;
        var baseUrl = addresses.Should().ContainSingle().Subject;
        return new MapboxStub(application, $"{baseUrl.TrimEnd('/')}/");
    }

    /// <summary>Holds the running Mapbox stub and its dynamically allocated loopback address.</summary>
    private sealed record MapboxStub(WebApplication Server, string BaseUrl);
}
