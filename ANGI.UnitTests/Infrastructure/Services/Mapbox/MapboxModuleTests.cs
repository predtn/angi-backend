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
    public sealed class MapboxModuleTests
    {
        // TEST-01: Bind Mapbox settings and apply base address and timeout to the typed IRouteService client.
        /// <summary>Verifies the Mapbox infrastructure module's settings and typed-client configuration.</summary>
        [Fact]
        public async Task AddMapboxInfrastructure_WithConfiguration_ShouldConfigureTypedClientAndSettings()
        {
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
            var handler = new WaitingHttpMessageHandler();

            services.AddMapboxInfrastructure(configuration);
            services.AddHttpClient<IRouteService, MapboxRouteService>()
                .ConfigurePrimaryHttpMessageHandler(() => handler);
            using var provider = services.BuildServiceProvider();

            var service = provider.GetRequiredService<IRouteService>();
            provider.GetRequiredService<IOptions<MapboxSettings>>().Value.Should().BeEquivalentTo(new MapboxSettings
            {
                BaseUrl = "https://mapbox.example/",
                AccessToken = "secret",
                TimeoutMilliseconds = 50,
                CacheHours = 12
            });

            // The guard only stops a hung test; a slow CI runner must not fail it, so it is not a timing assertion.
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await service.CalculateRouteAsync(
                null,
                [new RouteCoordinate(0m, 0m), new RouteCoordinate(0m, 0.01m)],
                RouteProfile.Driving,
                guard.Token);

            service.Should().BeOfType<MapboxRouteService>();
            result.IsEstimated.Should().BeTrue();
            handler.RequestUri.Should().Be(new Uri(
                "https://mapbox.example/directions/v5/mapbox/driving/0,0;0.01,0?access_token=secret&overview=false&steps=false"));
            // The handler waits forever, so only the 50 ms client timeout can have ended the call (the service rethrows
            // a cancellation that comes from the caller's token instead of estimating).
            handler.WasCanceled.Should().BeTrue();
            guard.IsCancellationRequested.Should().BeFalse();
        }

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
