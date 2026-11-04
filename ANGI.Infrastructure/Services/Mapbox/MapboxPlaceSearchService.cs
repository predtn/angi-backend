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
            if (_httpClient.BaseAddress is null || string.IsNullOrWhiteSpace(_settings.AccessToken))
            {
                _logger.LogWarning("Mapbox is not configured; places outside catalog addresses cannot be located.");
                return [];
            }

            var proximity = nearLatitude is { } latitude && nearLongitude is { } longitude
                ? string.Create(CultureInfo.InvariantCulture, $"{longitude},{latitude}")
                : null;
            var cacheKey = $"mapbox-place:{proximity}:{query.ToUpperInvariant()}";
            if (_cache.TryGetValue(cacheKey, out AgentPlaceCandidate[]? cached) && cached is not null)
            {
                return cached;
            }

            var path = "search/searchbox/v1/forward" +
                       $"?q={Uri.EscapeDataString(query)}&access_token={Uri.EscapeDataString(_settings.AccessToken)}" +
                       $"&country=vn&language=vi&limit={CandidateLimit}" +
                       (proximity is null ? "" : $"&proximity={proximity}");
            try
            {
                using var response = await _httpClient.GetAsync(path, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Mapbox place search failed with status {StatusCode}.", (int)response.StatusCode);
                    return [];
                }

                var body = await response.Content.ReadFromJsonAsync<SearchResponse>(_jsonOptions, ct);
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
                return candidates;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Mapbox place search did not answer within {TimeoutMilliseconds} ms.", _settings.TimeoutMilliseconds);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning("Mapbox place search could not be reached: {Status}.", exception.StatusCode);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                _logger.LogWarning(exception, "Mapbox place search returned an unreadable body.");
            }

            return [];
        }

        private sealed record SearchResponse(SearchFeature[]? Features);
        private sealed record SearchFeature(SearchGeometry? Geometry, SearchProperties? Properties);
        private sealed record SearchGeometry(decimal[]? Coordinates);
        private sealed record SearchProperties(string? Name, string? Full_Address, string? Place_Formatted, string? Feature_Type);
    }
}
