using System.Net;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Infrastructure.Services.Mapbox;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ANGI.UnitTests.Infrastructure.Services.Mapbox
{
    /// <summary>Verifies the Mapbox module binds its settings and configures the typed route client.</summary>
    public sealed class MapboxModuleTests
    {
        // TEST-01: Bind Mapbox settings and apply base address and timeout to the typed IRouteService client.
        /// <summary>Verifies the Mapbox infrastructure module's settings and typed-client configuration.</summary>
        [Fact]
        public async Task AddMapboxInfrastructure_WithConfiguration_ShouldConfigureTypedClientAndSettings()
        {
            // STEP-01: Create values as Dictionary<string, string?>, create configuration through AddInMemoryCollection().Build and 2 more.
            var values = new Dictionary<string, string?>
            {
                ["Mapbox:BaseUrl"] = "https://mapbox.example/",
                ["Mapbox:AccessToken"] = "secret",
                ["Mapbox:TimeoutMilliseconds"] = "50",
                ["Mapbox:CacheHours"] = "12"
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            // STEP-02: Create the WaitingHttpMessageHandler under test as handler.
            var handler = new WaitingHttpMessageHandler();

            // STEP-03: Call services.AddMapboxInfrastructure, call services.AddHttpClient().ConfigurePrimaryHttpMessageHandler and 2 more.
            services.AddMapboxInfrastructure(configuration);
            services.AddHttpClient<IRouteService, MapboxRouteService>()
                .ConfigurePrimaryHttpMessageHandler(() => handler);
            using var provider = services.BuildServiceProvider();

            var service = provider.GetRequiredService<IRouteService>();
            // STEP-04: Assert provider.GetRequiredService().Value is equivalent to a new MapboxSettings.
            provider.GetRequiredService<IOptions<MapboxSettings>>().Value.Should().BeEquivalentTo(new MapboxSettings
            {
                BaseUrl = "https://mapbox.example/",
                AccessToken = "secret",
                TimeoutMilliseconds = 50,
                CacheHours = 12
            });

            // STEP-05: Create guard as CancellationTokenSource and get result from service.CalculateRouteAsync.
            // The guard only stops a hung test; a slow CI runner must not fail it, so it is not a timing assertion.
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await service.CalculateRouteAsync(
                null,
                [new RouteCoordinate(0m, 0m), new RouteCoordinate(0m, 0.01m)],
                RouteProfile.Driving,
                guard.Token);

            // STEP-06: Assert service is MapboxRouteService, result.IsEstimated is true, handler.RequestUri is a new Uri, handler.WasCanceled is true and 1 more.
            service.Should().BeOfType<MapboxRouteService>();
            result.IsEstimated.Should().BeTrue();
            handler.RequestUri.Should().Be(new Uri(
                "https://mapbox.example/directions/v5/mapbox/driving/0,0;0.01,0?access_token=secret&overview=false&steps=false"));
            // The handler waits forever, so only the 50 ms client timeout can have ended the call (the service rethrows
            // a cancellation that comes from the caller's token instead of estimating).
            handler.WasCanceled.Should().BeTrue();
            guard.IsCancellationRequested.Should().BeFalse();
        }

        /// <summary>Handler that records the request and waits until cancelled, so the client timeout can be observed.</summary>
        private sealed class WaitingHttpMessageHandler : HttpMessageHandler
        {
            public Uri? RequestUri { get; private set; }

            public bool WasCanceled { get; private set; }

            /// <summary>Waits for the configured HttpClient timeout after capturing the absolute request URI.</summary>
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestUri = request.RequestUri;
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    WasCanceled = true;
                    throw;
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }
    }
}
