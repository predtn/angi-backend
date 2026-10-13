using System.Net;
using System.Text;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Infrastructure.Services.Mapbox;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ANGI.Test.Infrastructure.Services.Mapbox
{
    public sealed class MapboxRouteServiceTests
    {
        // TEST-01: Map a successful Mapbox response, round leg metrics, and send one request for the whole day.
        /// <summary>Verifies the success path and the complete Mapbox Directions request contract.</summary>
        [Fact]
        public async Task CalculateRouteAsync_WithMapboxResponse_ShouldMapAllLegMetrics()
        {
            var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, """
                {"routes":[{"legs":[
                    {"distance":2350.4,"duration":540.5},
                    {"distance":3100.6,"duration":660.4}
                ]}]}
                """));
            var service = CreateService(handler, accessToken: "secret token");

            var result = await service.CalculateRouteAsync(
                new RouteCoordinate(16.061204m, 108.227531m),
                [
                    new RouteCoordinate(16.067m, 108.220m),
                    new RouteCoordinate(16.074m, 108.215m)
                ],
                RouteProfile.Driving,
                CancellationToken.None);

            result.Legs.Should().Equal(
                new RouteMetric(2350, 541),
                new RouteMetric(3101, 660));
            result.IsEstimated.Should().BeFalse();
            var request = handler.Requests.Should().ContainSingle().Subject;
            request.Method.Should().Be(HttpMethod.Get);
            request.Uri.Should().Be(
                new Uri("https://api.mapbox.test/directions/v5/mapbox/driving/108.227531,16.061204;108.22,16.067;108.215,16.074?access_token=secret%20token&overview=false&steps=false"));
        }

        // TEST-02: Return no legs and avoid Mapbox when the day has fewer than two effective points.
        /// <summary>Verifies both empty-day and one-destination lower boundaries.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public async Task CalculateRouteAsync_WithFewerThanTwoPoints_ShouldReturnEmptyWithoutCallingMapbox(int count)
        {
            var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Must not be called."));
            var service = CreateService(handler);
            var destinations = count == 0
                ? Array.Empty<RouteCoordinate>()
                : new[] { new RouteCoordinate(16m, 108m) };

            var result = await service.CalculateRouteAsync(
                null,
                destinations,
                RouteProfile.Driving,
                CancellationToken.None);

            result.Should().Be(new RouteCalculationResult(Array.Empty<RouteMetric>(), false));
            handler.Requests.Should().BeEmpty();
        }

        // TEST-03: Include the origin selected by Application without applying roadmap business rules.
        /// <summary>Verifies that Infrastructure routes the caller's point sequence even when the origin is distant.</summary>
        [Fact]
        public async Task CalculateRouteAsync_WithProvidedOrigin_ShouldIncludeOriginWithoutBusinessFiltering()
        {
            var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
                "{\"routes\":[{\"legs\":[{\"distance\":29999,\"duration\":1200}]}]}"));
            var service = CreateService(handler);

            var result = await service.CalculateRouteAsync(
                new RouteCoordinate(10m, 0m),
                [new RouteCoordinate(0m, 0m)],
                RouteProfile.Driving,
                CancellationToken.None);

            result.Legs.Should().Equal(new RouteMetric(29999, 1200));
            handler.Requests.Single().Uri!.AbsolutePath.Should().EndWith("/0,10;0,0");
        }

        // TEST-04: Route directly between destinations when Application supplies no origin.
        /// <summary>Verifies the no-origin point sequence used by RM-14 for none or skipped origins.</summary>
        [Fact]
        public async Task CalculateRouteAsync_WithoutOrigin_ShouldStartAtFirstDestination()
        {
            var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
                "{\"routes\":[{\"legs\":[{\"distance\":1000,\"duration\":100}]}]}"));
            var service = CreateService(handler);

            var result = await service.CalculateRouteAsync(
                null,
                [
                    new RouteCoordinate(0m, 0m),
                    new RouteCoordinate(0.01m, 0m)
                ],
                RouteProfile.Driving,
                CancellationToken.None);

            result.Legs.Should().Equal(new RouteMetric(1000, 100));
            handler.Requests.Single().Uri!.AbsolutePath.Should().EndWith("/0,0;0,0.01");
        }

        // TEST-05: Accept exactly 25 effective points, the Mapbox Directions upper boundary.
        /// <summary>Verifies that the documented maximum point count is accepted in one request.</summary>
        [Fact]
        public async Task CalculateRouteAsync_WithTwentyFivePoints_ShouldCallMapboxOnce()
        {
            var legs = string.Join(',', Enumerable.Repeat("{\"distance\":1,\"duration\":1}", 24));
            var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
                $"{{\"routes\":[{{\"legs\":[{legs}]}}]}}"));
            var service = CreateService(handler);
            var destinations = Enumerable.Range(1, 24)
                .Select(index => new RouteCoordinate(0m, index / 1000m))
                .ToArray();

            var result = await service.CalculateRouteAsync(
                new RouteCoordinate(0m, 0m),
                destinations,
                RouteProfile.Driving,
                CancellationToken.None);

            result.Legs.Should().HaveCount(24);
            handler.Requests.Should().ContainSingle();
        }

        // TEST-06: Reject 26 effective points before issuing an unsupported Mapbox request.
        /// <summary>Verifies the first value above the documented maximum point count.</summary>
        [Fact]
        public async Task CalculateRouteAsync_WithMoreThanTwentyFivePoints_ShouldThrowArgumentOutOfRange()
        {
            var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Must not be called."));
            var service = CreateService(handler);
            var destinations = Enumerable.Range(1, 25)
                .Select(index => new RouteCoordinate(0m, index / 1000m))
                .ToArray();

            var act = () => service.CalculateRouteAsync(
                new RouteCoordinate(0m, 0m),
                destinations,
                RouteProfile.Driving,
                CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
            handler.Requests.Should().BeEmpty();
        }

        // TEST-07: Reuse successful metrics for origins in the same 3-decimal cache cell.
        /// <summary>Verifies origin rounding and cache reuse without coupling metrics to roadmap identifiers.</summary>
        [Fact]
        public async Task CalculateRouteAsync_WithEquivalentRoundedOrigin_ShouldUseCachedMetrics()
        {
            var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
                "{\"routes\":[{\"legs\":[{\"distance\":500,\"duration\":60}]}]}"));
            var service = CreateService(handler);
            var destination = new RouteCoordinate(16.01m, 108.01m);

            await service.CalculateRouteAsync(
                new RouteCoordinate(16.00011m, 108.00011m),
                [destination],
                RouteProfile.Walking,
                CancellationToken.None);
            var cached = await service.CalculateRouteAsync(
                new RouteCoordinate(16.00012m, 108.00012m),
                [destination],
                RouteProfile.Walking,
                CancellationToken.None);

            handler.Requests.Should().ContainSingle();
            cached.Legs.Should().Equal(new RouteMetric(500, 60));
        }

        // TEST-08: Keep origins on opposite sides of the 3-decimal rounding boundary in separate cache entries.
        /// <summary>Verifies the first values below and at the cache-key rounding midpoint.</summary>
        [Fact]
        public async Task CalculateRouteAsync_WithOriginsAcrossRoundingBoundary_ShouldCallMapboxTwice()
        {
            var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
                "{\"routes\":[{\"legs\":[{\"distance\":500,\"duration\":60}]}]}"));
            var service = CreateService(handler);
            var destination = new RouteCoordinate(16.01m, 108.01m);

            await service.CalculateRouteAsync(
                new RouteCoordinate(16.00049m, 108.00049m),
                [destination],
                RouteProfile.Walking,
                CancellationToken.None);
            await service.CalculateRouteAsync(
                new RouteCoordinate(16.00050m, 108.00050m),
                [destination],
                RouteProfile.Walking,
                CancellationToken.None);

            handler.Requests.Should().HaveCount(2);
        }

        // TEST-09: Keep a route until just before CacheHours and refresh it at the expiration boundary.
        /// <summary>Verifies both sides of the configured absolute cache-expiration boundary.</summary>
        [Fact]
        public async Task CalculateRouteAsync_AcrossCacheExpiration_ShouldRefreshOnlyAtExpiration()
        {
            var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
                "{\"routes\":[{\"legs\":[{\"distance\":500,\"duration\":60}]}]}"));
            var clock = new TestSystemClock(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));
            using var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
            var service = CreateService(handler, cache: cache, cacheHours: 1);
            RouteCoordinate[] destinations =
            [
                new RouteCoordinate(0m, 0m),
                new RouteCoordinate(0.01m, 0m)
            ];

            await service.CalculateRouteAsync(null, destinations, RouteProfile.Driving, CancellationToken.None);
            clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));
            await service.CalculateRouteAsync(null, destinations, RouteProfile.Driving, CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(1));
            await service.CalculateRouteAsync(null, destinations, RouteProfile.Driving, CancellationToken.None);

            handler.Requests.Should().HaveCount(2);
        }

        // TEST-10: Keep routes with different profiles in separate cache entries.
        /// <summary>Verifies that profile participates in the cache key.</summary>
        [Fact]
        public async Task CalculateRouteAsync_WithDifferentProfiles_ShouldCallMapboxForEachProfile()
        {
            var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
                "{\"routes\":[{\"legs\":[{\"distance\":500,\"duration\":60}]}]}"));
            var service = CreateService(handler);
            RouteCoordinate[] destinations =
            [
                new RouteCoordinate(0m, 0m),
                new RouteCoordinate(0.01m, 0m)
            ];

            await service.CalculateRouteAsync(null, destinations, RouteProfile.Driving, CancellationToken.None);
            await service.CalculateRouteAsync(null, destinations, RouteProfile.Cycling, CancellationToken.None);

            handler.Requests.Should().HaveCount(2);
            handler.Requests.Select(request => request.Uri!.AbsolutePath).Should().Contain(path => path.Contains("/driving/"));
            handler.Requests.Select(request => request.Uri!.AbsolutePath).Should().Contain(path => path.Contains("/cycling/"));
        }

        // TEST-11: Estimate every leg when Mapbox is unavailable or returns an invalid response.
        /// <summary>Verifies all provider-failure regions that must use the Haversine fallback.</summary>
        [Theory]
        [InlineData("server-error")]
        [InlineData("network")]
        [InlineData("timeout")]
        [InlineData("bad-json")]
        [InlineData("missing-route")]
        [InlineData("wrong-leg-count")]
        [InlineData("negative-metric")]
        public async Task CalculateRouteAsync_WhenMapboxFails_ShouldEstimateRoute(string failure)
        {
            var service = CreateFailingService(failure);

            var result = await service.CalculateRouteAsync(
                null,
                [
                    new RouteCoordinate(0m, 0m),
                    new RouteCoordinate(0m, 0.01m)
                ],
                RouteProfile.Driving,
                CancellationToken.None);

            result.IsEstimated.Should().BeTrue();
            result.Legs.Should().ContainSingle();
            result.Legs[0].DistanceMeters.Should().Be(1446);
            result.Legs[0].DurationSeconds.Should().Be(208);
        }

        // TEST-12: Use the documented 25, 12 and 4 km/h fallback speeds for each travel profile.
        /// <summary>Verifies every supported profile's estimated duration region.</summary>
        [Theory]
        [InlineData(RouteProfile.Driving, 208)]
        [InlineData(RouteProfile.Cycling, 434)]
        [InlineData(RouteProfile.Walking, 1301)]
        public async Task CalculateRouteAsync_WhenEstimating_ShouldUseProfileSpeed(
            RouteProfile profile,
            int expectedDurationSeconds)
        {
            var service = CreateService(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

            var result = await service.CalculateRouteAsync(
                null,
                [
                    new RouteCoordinate(0m, 0m),
                    new RouteCoordinate(0m, 0.01m)
                ],
                profile,
                CancellationToken.None);

            result.Legs.Single().DurationSeconds.Should().Be(expectedDurationSeconds);
        }

        // TEST-13: Estimate instead of calling Mapbox when its base URL or access token is absent.
        /// <summary>Verifies both missing-configuration branches.</summary>
        [Theory]
        [InlineData("", "token")]
        [InlineData("https://api.mapbox.test/", "")]
        public async Task CalculateRouteAsync_WhenMapboxIsNotConfigured_ShouldEstimateWithoutSending(
            string baseUrl,
            string accessToken)
        {
            var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Must not be called."));
            var service = CreateService(handler, baseUrl, accessToken);

            var result = await service.CalculateRouteAsync(
                null,
                [new RouteCoordinate(0m, 0m), new RouteCoordinate(0m, 0.01m)],
                RouteProfile.Driving,
                CancellationToken.None);

            result.IsEstimated.Should().BeTrue();
            handler.Requests.Should().BeEmpty();
        }

        // TEST-14: Do not cache fallback metrics so Mapbox can recover on the next request.
        /// <summary>Verifies that a transient provider failure does not poison the 24-hour cache.</summary>
        [Fact]
        public async Task CalculateRouteAsync_AfterTransientFailure_ShouldRetryMapbox()
        {
            var callCount = 0;
            var handler = new StubHttpMessageHandler(_ => ++callCount == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json(HttpStatusCode.OK, "{\"routes\":[{\"legs\":[{\"distance\":10,\"duration\":2}]}]}"));
            var service = CreateService(handler);
            RouteCoordinate[] destinations =
            [
                new RouteCoordinate(0m, 0m),
                new RouteCoordinate(0m, 0.01m)
            ];

            var first = await service.CalculateRouteAsync(null, destinations, RouteProfile.Driving, CancellationToken.None);
            var second = await service.CalculateRouteAsync(null, destinations, RouteProfile.Driving, CancellationToken.None);

            first.IsEstimated.Should().BeTrue();
            second.IsEstimated.Should().BeFalse();
            handler.Requests.Should().HaveCount(2);
        }

        // TEST-15: Propagate caller cancellation instead of converting it into an estimated route.
        /// <summary>Verifies cancellation ownership at the HttpClient boundary.</summary>
        [Fact]
        public async Task CalculateRouteAsync_WhenCallerCancels_ShouldThrowOperationCanceled()
        {
            using var cts = new CancellationTokenSource();
            var handler = new StubHttpMessageHandler(_ =>
            {
                cts.Cancel();
                throw new TaskCanceledException("Canceled by caller.");
            });
            var service = CreateService(handler);

            var act = () => service.CalculateRouteAsync(
                null,
                [new RouteCoordinate(0m, 0m), new RouteCoordinate(0m, 0.01m)],
                RouteProfile.Driving,
                cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        // TEST-16: Reject an enum value outside the three route profiles.
        /// <summary>Verifies the unsupported-profile boundary before an HTTP request is sent.</summary>
        [Fact]
        public async Task CalculateRouteAsync_WithUnsupportedProfile_ShouldThrowArgumentOutOfRange()
        {
            var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Must not be called."));
            var service = CreateService(handler);

            var act = () => service.CalculateRouteAsync(
                null,
                [new RouteCoordinate(0m, 0m), new RouteCoordinate(0m, 0.01m)],
                (RouteProfile)99,
                CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
            handler.Requests.Should().BeEmpty();
        }

        /// <summary>Creates a service whose handler represents one documented provider-failure shape.</summary>
        private static MapboxRouteService CreateFailingService(string failure) => failure switch
        {
            "server-error" => CreateService(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))),
            "network" => CreateService(new StubHttpMessageHandler(_ => throw new HttpRequestException("Unavailable"))),
            "timeout" => CreateService(new StubHttpMessageHandler(_ => throw new TaskCanceledException("Timeout"))),
            "bad-json" => CreateService(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "not-json"))),
            "missing-route" => CreateService(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{\"routes\":[]}"))),
            "wrong-leg-count" => CreateService(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
                "{\"routes\":[{\"legs\":[]}]}"))),
            "negative-metric" => CreateService(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
                "{\"routes\":[{\"legs\":[{\"distance\":-1,\"duration\":1}]}]}"))),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };

        /// <summary>Creates the system under test with isolated HTTP and memory-cache dependencies.</summary>
        private static MapboxRouteService CreateService(
            HttpMessageHandler handler,
            string baseUrl = "https://api.mapbox.test/",
            string accessToken = "test-token",
            IMemoryCache? cache = null,
            int cacheHours = 24)
        {
            var client = new HttpClient(handler);
            if (baseUrl.Length > 0)
            {
                client.BaseAddress = new Uri(baseUrl);
            }

            return new MapboxRouteService(
                client,
                cache ?? new MemoryCache(new MemoryCacheOptions()),
                Options.Create(new MapboxSettings
                {
                    BaseUrl = baseUrl,
                    AccessToken = accessToken,
                    TimeoutMilliseconds = 3000,
                    CacheHours = cacheHours
                }),
                NullLogger<MapboxRouteService>.Instance);
        }

        /// <summary>Creates a JSON HTTP response for a Mapbox stub.</summary>
        private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) =>
            new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private sealed record CapturedRequest(HttpMethod Method, Uri? Uri);

        private sealed class TestSystemClock : ISystemClock
        {
            /// <summary>Creates a controllable cache clock at the supplied UTC instant.</summary>
            public TestSystemClock(DateTimeOffset utcNow)
            {
                UtcNow = utcNow;
            }

            public DateTimeOffset UtcNow { get; private set; }

            /// <summary>Advances the cache clock by the specified duration.</summary>
            public void Advance(TimeSpan duration) => UtcNow += duration;
        }

        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _send;

            /// <summary>Creates a handler that delegates responses to the supplied test function.</summary>
            public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
            {
                _send = send;
            }

            public List<CapturedRequest> Requests { get; } = [];

            /// <summary>Captures each request before returning the configured response.</summary>
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Requests.Add(new CapturedRequest(request.Method, request.RequestUri));
                return Task.FromResult(_send(request));
            }
        }
    }
}
