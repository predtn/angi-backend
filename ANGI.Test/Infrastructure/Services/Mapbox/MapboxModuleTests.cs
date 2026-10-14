using System.Diagnostics;
using System.Net;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Infrastructure.Services.Mapbox;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ANGI.Test.Infrastructure.Services.Mapbox
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

            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var stopwatch = Stopwatch.StartNew();
            var result = await service.CalculateRouteAsync(
                null,
                [new RouteCoordinate(0m, 0m), new RouteCoordinate(0m, 0.01m)],
                RouteProfile.Driving,
                guard.Token);

            stopwatch.Stop();
            service.Should().BeOfType<MapboxRouteService>();
            result.IsEstimated.Should().BeTrue();
            handler.RequestUri.Should().Be(new Uri(
                "https://mapbox.example/directions/v5/mapbox/driving/0,0;0.01,0?access_token=secret&overview=false&steps=false"));
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        }

        private sealed class WaitingHttpMessageHandler : HttpMessageHandler
        {
            public Uri? RequestUri { get; private set; }

            /// <summary>Waits for the configured HttpClient timeout after capturing the absolute request URI.</summary>
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestUri = request.RequestUri;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }
    }
}
