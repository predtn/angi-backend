using System.Net;
using System.Text;
using ANGI.Infrastructure.Services.Mapbox;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ANGI.UnitTests.Infrastructure.Services.Mapbox
{
    /// <summary>Verifies place search reads Mapbox features, caches answers and never fails a chat turn when Mapbox cannot answer.</summary>
    public sealed class MapboxPlaceSearchServiceTests
    {
        private const string TwoFeatures = """
            {"features":[
              {"geometry":{"coordinates":[105.50493,21.0445]},"properties":{"name":"Hoa Lac","place_formatted":"Hà Nội, Việt Nam","feature_type":"locality"}},
              {"geometry":{"coordinates":[105.8,21.0]},"properties":{"name":"Times City","full_address":"458 Minh Khai, Hà Nội","feature_type":"poi"}},
              {"geometry":{"coordinates":[105.8]},"properties":{"name":"Thiếu tọa độ"}},
              {"geometry":{"coordinates":[105.8,21.0]},"properties":{"name":null}}
            ]}
            """;

        // TEST-01: Features map to candidates with latitude and longitude the right way round, the query and proximity are encoded, and unusable features are dropped.
        /// <summary>Verifies the request contract and the longitude-first response order.</summary>
        [Fact]
        public async Task SearchAsync_Features_MapToCandidates()
        {
            var handler = new StubHandler(_ => Json(HttpStatusCode.OK, TwoFeatures));
            var service = Create(handler);

            var result = await service.SearchAsync("Hòa Lạc, Hà Nội", 21.03m, 105.85m, default);

            result.Should().HaveCount(2);
            result[0].Should().Match<ANGI.Application.Common.Models.Agent.AgentPlaceCandidate>(x => x.Name == "Hoa Lac" && x.Latitude == 21.0445m && x.Longitude == 105.50493m &&
                x.Type == "locality" && x.Address == "Hà Nội, Việt Nam");
            result[1].Address.Should().Be("458 Minh Khai, Hà Nội");
            var uri = handler.Requests.Single().AbsoluteUri;
            uri.Should().StartWith("https://api.mapbox.test/search/searchbox/v1/forward?")
                .And.Contain("access_token=test-token").And.Contain("country=vn").And.Contain("proximity=105.85,21.03")
                .And.Contain(Uri.EscapeDataString("Hòa Lạc, Hà Nội"));
        }

        // TEST-02: Repeating a place within the cache window sends no second request; another place does.
        /// <summary>Verifies a popular place costs one Mapbox request.</summary>
        [Fact]
        public async Task SearchAsync_SameQuery_UsesCache()
        {
            var handler = new StubHandler(_ => Json(HttpStatusCode.OK, TwoFeatures));
            var service = Create(handler);

            await service.SearchAsync("Hòa Lạc", null, null, default);
            await service.SearchAsync("hòa lạc", null, null, default);
            await service.SearchAsync("Times City", null, null, default);

            handler.Requests.Should().HaveCount(2);
        }

        // TEST-03: Every failure returns no candidates and is not cached; covers 5xx, 401, a network error, a client timeout and an unreadable body.
        /// <summary>Verifies Mapbox failures become "place unknown" instead of a failed chat turn.</summary>
        [Theory]
        [InlineData("server")][InlineData("unauthorized")][InlineData("network")][InlineData("timeout")][InlineData("body")]
        public async Task SearchAsync_ProviderFailure_ReturnsNoCandidates(string failure)
        {
            var calls = 0;
            var handler = new StubHandler(_ =>
            {
                calls++;
                return failure switch
                {
                    "server" => Json(HttpStatusCode.ServiceUnavailable, "{}"),
                    "unauthorized" => Json(HttpStatusCode.Unauthorized, "{}"),
                    "network" => throw new HttpRequestException("down"),
                    "timeout" => throw new TaskCanceledException("timeout"),
                    _ => Json(HttpStatusCode.OK, "not json")
                };
            });
            var service = Create(handler);

            (await service.SearchAsync("Hòa Lạc", null, null, default)).Should().BeEmpty();
            await service.SearchAsync("Hòa Lạc", null, null, default);

            calls.Should().Be(2);
        }

        // TEST-04: Without a token or base address no request is sent.
        /// <summary>Verifies an unconfigured environment stays catalog-only.</summary>
        [Theory]
        [InlineData("", "https://api.mapbox.test/")]
        [InlineData("test-token", "")]
        public async Task SearchAsync_NotConfigured_SendsNothing(string token, string baseUrl)
        {
            var handler = new StubHandler(_ => throw new InvalidOperationException("Must not be called."));

            (await Create(handler, token, baseUrl).SearchAsync("Hòa Lạc", null, null, default)).Should().BeEmpty();
        }

        // TEST-05: The caller's cancellation is rethrown rather than treated as an unknown place.
        /// <summary>Verifies a cancelled turn stops instead of continuing without the place.</summary>
        [Fact]
        public async Task SearchAsync_CallerCancelled_Rethrows()
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            var handler = new StubHandler(_ => throw new OperationCanceledException(cancelled.Token));

            var act = () => Create(handler).SearchAsync("Hòa Lạc", null, null, cancelled.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        // TEST-06: The access token never reaches the logs, even when the HTTP error text carries the request address.
        /// <summary>Verifies a failed search cannot leak the Mapbox token through its log message or exception.</summary>
        [Fact]
        public async Task SearchAsync_NetworkError_DoesNotLogToken()
        {
            var logged = new List<string>();
            var handler = new StubHandler(request => throw new HttpRequestException($"failed {request.RequestUri}"));
            var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.mapbox.test/") };
            var service = new MapboxPlaceSearchService(client, new MemoryCache(new MemoryCacheOptions()),
                Options.Create(new MapboxSettings { AccessToken = "secret-token" }), new CapturingLogger(logged));

            await service.SearchAsync("Hòa Lạc", null, null, default);

            logged.Should().NotBeEmpty().And.NotContain(line => line.Contains("secret-token"));
        }

        /// <summary>Creates the service over a stub transport.</summary>
        private static MapboxPlaceSearchService Create(StubHandler handler, string token = "test-token", string baseUrl = "https://api.mapbox.test/")
        {
            var client = new HttpClient(handler);
            if (baseUrl.Length > 0) client.BaseAddress = new Uri(baseUrl);
            return new(client, new MemoryCache(new MemoryCacheOptions()), Options.Create(new MapboxSettings { AccessToken = token }), NullLogger<MapboxPlaceSearchService>.Instance);
        }

        /// <summary>Builds a JSON response.</summary>
        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        /// <summary>Records requests and delegates the response to the test.</summary>
        private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
        {
            public List<Uri> Requests { get; } = [];

            /// <summary>Captures the request before answering.</summary>
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri!);
                return Task.FromResult(send(request));
            }
        }

        /// <summary>Keeps every formatted log line and exception text so a test can search them.</summary>
        private sealed class CapturingLogger(List<string> lines) : ILogger<MapboxPlaceSearchService>
        {
            /// <summary>No scope is needed for capturing.</summary>
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            /// <summary>Every level is captured.</summary>
            public bool IsEnabled(LogLevel logLevel) => true;

            /// <summary>Stores the message together with any exception text.</summary>
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Add($"{formatter(state, exception)} {exception}");
        }
    }
}
