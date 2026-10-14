using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Models.Roadmap;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ANGI.Infrastructure.Services.Mapbox
{
    /// <summary>
    /// Calls Mapbox Directions once for a roadmap day, caches successful metrics, and estimates a route when Mapbox
    /// cannot answer.
    /// </summary>
    public sealed class MapboxRouteService : IRouteService
    {
        internal const int MaxPoints = 25;

        private const double EarthRadiusMeters = 6_371_000d;
        private const double RoadDistanceFactor = 1.3d;
        private const int DefaultCacheHours = 24;

        private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly MapboxSettings _settings;
        private readonly ILogger<MapboxRouteService> _logger;

        /// <summary>Creates the Mapbox client with its HTTP transport, cache, settings and logger.</summary>
        public MapboxRouteService(
            HttpClient httpClient,
            IMemoryCache cache,
            IOptions<MapboxSettings> settings,
            ILogger<MapboxRouteService> logger)
        {
            _httpClient = httpClient;
            _cache = cache;
            _settings = settings.Value;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<RouteCalculationResult> CalculateRouteAsync(
            RouteCoordinate? origin,
            IReadOnlyList<RouteCoordinate> destinations,
            RouteProfile profile,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(destinations);
            _ = ProfileName(profile);

            var points = BuildPoints(origin, destinations);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(points.Count, MaxPoints, nameof(destinations));

            if (points.Count < 2)
            {
                return Empty();
            }

            var cacheKey = BuildCacheKey(profile, points, origin is not null);
            if (_cache.TryGetValue(cacheKey, out CachedRoute? cachedRoute) && cachedRoute is not null)
            {
                return new RouteCalculationResult(cachedRoute.Legs, false);
            }

            var metrics = await GetMapboxLegsAsync(points, profile, ct);
            if (metrics is not null)
            {
                _cache.Set(
                    cacheKey,
                    new CachedRoute(metrics),
                    TimeSpan.FromHours(_settings.CacheHours > 0 ? _settings.CacheHours : DefaultCacheHours));
                return new RouteCalculationResult(metrics, false);
            }

            return new RouteCalculationResult(EstimateLegs(points, profile), true);
        }

        /// <summary>Builds the provider point sequence from the caller's already-decided origin and destinations.</summary>
        private static IReadOnlyList<RouteCoordinate> BuildPoints(
            RouteCoordinate? origin,
            IReadOnlyList<RouteCoordinate> destinations)
        {
            var points = new List<RouteCoordinate>(destinations.Count + (origin is null ? 0 : 1));
            if (origin is not null)
            {
                points.Add(origin);
            }

            points.AddRange(destinations);
            return points;
        }

        /// <summary>Calls Mapbox Directions and returns null for every provider failure except caller cancellation.</summary>
        private async Task<IReadOnlyList<RouteMetric>?> GetMapboxLegsAsync(
            IReadOnlyList<RouteCoordinate> points,
            RouteProfile profile,
            CancellationToken ct)
        {
            if (_httpClient.BaseAddress is null || string.IsNullOrWhiteSpace(_settings.AccessToken))
            {
                _logger.LogWarning("Mapbox is not configured; route metrics will be estimated.");
                return null;
            }

            var path = BuildRequestPath(points, profile, _settings.AccessToken);
            try
            {
                using var response = await _httpClient.GetAsync(path, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Mapbox Directions failed with status {StatusCode}; route metrics will be estimated.",
                        (int)response.StatusCode);
                    return null;
                }

                var body = await response.Content.ReadFromJsonAsync<MapboxResponse>(_jsonOptions, ct);
                var legs = body?.Routes?.FirstOrDefault()?.Legs;
                if (legs is null || legs.Count != points.Count - 1)
                {
                    _logger.LogWarning("Mapbox Directions returned an invalid leg count; route metrics will be estimated.");
                    return null;
                }

                var metrics = new List<RouteMetric>(legs.Count);
                foreach (var leg in legs)
                {
                    if (!TryRoundMetric(leg.Distance, out var distance) ||
                        !TryRoundMetric(leg.Duration, out var duration))
                    {
                        _logger.LogWarning("Mapbox Directions returned invalid route metrics; route metrics will be estimated.");
                        return null;
                    }

                    metrics.Add(new RouteMetric(distance, duration));
                }

                return metrics;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Mapbox Directions did not answer within {TimeoutMilliseconds} ms; route metrics will be estimated.",
                    _settings.TimeoutMilliseconds);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning(exception, "Mapbox Directions could not be reached; route metrics will be estimated.");
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                _logger.LogWarning(exception, "Mapbox Directions returned an unreadable body; route metrics will be estimated.");
            }

            return null;
        }

        /// <summary>Builds a Mapbox Directions request with longitude before latitude as required by Mapbox.</summary>
        private static string BuildRequestPath(
            IReadOnlyList<RouteCoordinate> points,
            RouteProfile profile,
            string accessToken)
        {
            var coordinates = string.Join(';', points.Select(point =>
                $"{FormatCoordinate(point.Longitude)},{FormatCoordinate(point.Latitude)}"));
            return $"directions/v5/mapbox/{ProfileName(profile)}/{coordinates}" +
                   $"?access_token={Uri.EscapeDataString(accessToken)}&overview=false&steps=false";
        }

        /// <summary>Builds a cache key from profile and coordinates, rounding only the optional origin to 3 decimals.</summary>
        private static string BuildCacheKey(
            RouteProfile profile,
            IReadOnlyList<RouteCoordinate> points,
            bool hasOrigin)
        {
            var coordinates = points.Select((point, index) =>
            {
                var latitude = hasOrigin && index == 0
                    ? decimal.Round(point.Latitude, 3, MidpointRounding.AwayFromZero)
                    : point.Latitude;
                var longitude = hasOrigin && index == 0
                    ? decimal.Round(point.Longitude, 3, MidpointRounding.AwayFromZero)
                    : point.Longitude;
                return $"{FormatCoordinate(longitude)},{FormatCoordinate(latitude)}";
            });

            return $"mapbox-route:{ProfileName(profile)}:{string.Join(';', coordinates)}";
        }

        /// <summary>Estimates every leg as straight-line distance times 1.3 at the profile's documented speed.</summary>
        private static IReadOnlyList<RouteMetric> EstimateLegs(
            IReadOnlyList<RouteCoordinate> points,
            RouteProfile profile)
        {
            var speedMetersPerSecond = SpeedKilometersPerHour(profile) / 3.6d;
            var legs = new List<RouteMetric>(points.Count - 1);
            for (var index = 1; index < points.Count; index++)
            {
                var from = points[index - 1];
                var to = points[index];
                var distance = HaversineMeters(from.Latitude, from.Longitude, to.Latitude, to.Longitude) *
                               RoadDistanceFactor;
                legs.Add(new RouteMetric(RoundMetric(distance), RoundMetric(distance / speedMetersPerSecond)));
            }

            return legs;
        }

        /// <summary>Returns the contract result for a day with fewer than two effective points.</summary>
        private static RouteCalculationResult Empty() =>
            new(Array.Empty<RouteMetric>(), false);

        /// <summary>Calculates straight-line distance between two WGS84 coordinate pairs.</summary>
        private static double HaversineMeters(
            decimal fromLatitude,
            decimal fromLongitude,
            decimal toLatitude,
            decimal toLongitude)
        {
            var latitude1 = DegreesToRadians((double)fromLatitude);
            var latitude2 = DegreesToRadians((double)toLatitude);
            var latitudeDelta = latitude2 - latitude1;
            var longitudeDelta = DegreesToRadians((double)(toLongitude - fromLongitude));
            var haversine = Math.Pow(Math.Sin(latitudeDelta / 2d), 2d) +
                            Math.Cos(latitude1) * Math.Cos(latitude2) *
                            Math.Pow(Math.Sin(longitudeDelta / 2d), 2d);
            return EarthRadiusMeters * 2d * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1d - haversine));
        }

        /// <summary>Converts degrees to radians for Haversine calculations.</summary>
        private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

        /// <summary>Returns the Mapbox profile name used in the request path.</summary>
        private static string ProfileName(RouteProfile profile) => profile switch
        {
            RouteProfile.Driving => "driving",
            RouteProfile.Walking => "walking",
            RouteProfile.Cycling => "cycling",
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unsupported route profile.")
        };

        /// <summary>Returns the fallback speed documented for each supported profile.</summary>
        private static double SpeedKilometersPerHour(RouteProfile profile) => profile switch
        {
            RouteProfile.Driving => 25d,
            RouteProfile.Walking => 4d,
            RouteProfile.Cycling => 12d,
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unsupported route profile.")
        };

        /// <summary>Formats coordinates without culture-specific decimal separators or needless trailing zeroes.</summary>
        private static string FormatCoordinate(decimal coordinate) =>
            coordinate.ToString("0.############################", CultureInfo.InvariantCulture);

        /// <summary>Rounds a finite, non-negative metric to the nearest API integer.</summary>
        private static bool TryRoundMetric(double value, out int rounded)
        {
            if (!double.IsFinite(value) || value < 0d || value > int.MaxValue)
            {
                rounded = 0;
                return false;
            }

            rounded = RoundMetric(value);
            return true;
        }

        /// <summary>Rounds a route metric using deterministic midpoint-away-from-zero behavior.</summary>
        private static int RoundMetric(double value) =>
            checked((int)Math.Round(value, MidpointRounding.AwayFromZero));

        private sealed record CachedRoute(IReadOnlyList<RouteMetric> Legs);
        private sealed record MapboxLeg(double Distance, double Duration);
        private sealed record MapboxRoute(IReadOnlyList<MapboxLeg>? Legs);
        private sealed record MapboxResponse(IReadOnlyList<MapboxRoute>? Routes);
    }
}
