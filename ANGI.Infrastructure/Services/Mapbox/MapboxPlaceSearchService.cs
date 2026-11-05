using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Models.Agent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ANGI.Infrastructure.Services.Mapbox
{
    /// <summary>
    /// Finds named places with the Mapbox Search Box API, caching answers so repeating a place costs no request, and returns
    /// no candidates whenever Mapbox cannot answer so that a chat turn never fails because of it.
    /// </summary>
    public sealed class MapboxPlaceSearchService : IPlaceSearchService
    {
        private const int DefaultCacheHours = 24;
        private const int CandidateLimit = 5;

        private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly MapboxSettings _settings;
        private readonly ILogger<MapboxPlaceSearchService> _logger;

        /// <summary>Creates the search client with its HTTP transport, cache, settings and logger.</summary>
        public MapboxPlaceSearchService(
            HttpClient httpClient,
            IMemoryCache cache,
            IOptions<MapboxSettings> settings,
            ILogger<MapboxPlaceSearchService> logger)
        {
            _httpClient = httpClient;
            _cache = cache;
            _settings = settings.Value;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<AgentPlaceCandidate[]> SearchAsync(
            string query,
            decimal? nearLatitude,
            decimal? nearLongitude,
            CancellationToken ct)
        {
            // STEP-01: Return the collected items early when _httpClient.BaseAddress is missing or _settings.AccessToken is blank.
            if (_httpClient.BaseAddress is null || string.IsNullOrWhiteSpace(_settings.AccessToken))
            {
                _logger.LogWarning("Mapbox is not configured; places outside catalog addresses cannot be located.");
                return [];
            }

            // STEP-02: Prepare proximity from string.Create and cacheKey from query.ToUpperInvariant.
            var proximity = nearLatitude is { } latitude && nearLongitude is { } longitude
                ? string.Create(CultureInfo.InvariantCulture, $"{longitude},{latitude}")
                : null;
            var cacheKey = $"mapbox-place:{proximity}:{query.ToUpperInvariant()}";
            // STEP-03: Return cached early when _cache has an entry for cacheKey and cached exists.
            if (_cache.TryGetValue(cacheKey, out AgentPlaceCandidate[]? cached) && cached is not null)
            {
                return cached;
            }

            // STEP-04: Prepare path from Uri.EscapeDataString.
            var path = "search/searchbox/v1/forward" +
                       $"?q={Uri.EscapeDataString(query)}&access_token={Uri.EscapeDataString(_settings.AccessToken)}" +
                       $"&country=vn&language=vi&limit={CandidateLimit}" +
                       (proximity is null ? "" : $"&proximity={proximity}");
            try
            {
                // STEP-05: Load response through httpClient.GetAsync.
                using var response = await _httpClient.GetAsync(path, ct);
                // STEP-06: Return the collected items early when response.IsSuccessStatusCode is false.
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Mapbox place search failed with status {StatusCode}.", (int)response.StatusCode);
                    return [];
                }

                // STEP-07: Load body through response.Content.ReadFromJsonAsync.
                var body = await response.Content.ReadFromJsonAsync<SearchResponse>(_jsonOptions, ct);
                // STEP-08: Prepare candidates, then call cache.Set.
                var candidates = (body?.Features ?? [])
                    .Where(feature => feature.Properties?.Name is not null && feature.Geometry?.Coordinates is { Length: 2 })
                    .Select(feature => new AgentPlaceCandidate(
                        feature.Properties!.Name!,
                        feature.Properties.Full_Address ?? feature.Properties.Place_Formatted,
                        feature.Properties.Feature_Type,
                        feature.Geometry!.Coordinates![1],
                        feature.Geometry.Coordinates[0]))
                    .ToArray();
                _cache.Set(cacheKey, candidates, TimeSpan.FromHours(_settings.CacheHours > 0 ? _settings.CacheHours : DefaultCacheHours));
                // STEP-09: Return candidates.
                return candidates;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // STEP-10: On OperationCanceledException when ct.IsCancellationRequested is false, call logger.LogWarning.
                _logger.LogWarning("Mapbox place search did not answer within {TimeoutMilliseconds} ms.", _settings.TimeoutMilliseconds);
            }
            catch (HttpRequestException exception)
            {
                // STEP-11: On HttpRequestException, call logger.LogWarning.
                _logger.LogWarning("Mapbox place search could not be reached: {Status}.", exception.StatusCode);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                // STEP-12: On Exception when exception is one of the listed values, call logger.LogWarning.
                _logger.LogWarning(exception, "Mapbox place search returned an unreadable body.");
            }

            // STEP-13: Return the collected items.
            return [];
        }

        private sealed record SearchResponse(SearchFeature[]? Features);
        private sealed record SearchFeature(SearchGeometry? Geometry, SearchProperties? Properties);
        private sealed record SearchGeometry(decimal[]? Coordinates);
        private sealed record SearchProperties(string? Name, string? Full_Address, string? Place_Formatted, string? Feature_Type);
    }
}
