namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Represents a restaurant summary card displayed in lists and search results.</summary>
public sealed class RestaurantCardDto
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string? CoverUrl { get; init; }
    public string AddressLine { get; init; } = string.Empty;
    public string? District { get; init; }
    public string? ProvinceName { get; init; }
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
    public short? PriceLevel { get; init; }
    public decimal RatingAvg { get; init; }
    public int RatingCount { get; init; }
    public string OperatingStatus { get; init; } = string.Empty;
    public bool IsOpenNow { get; init; }
    public decimal? DistanceKm { get; init; }
}
