using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Represents detailed information of a restaurant for guest and traveler view (REST-02).</summary>
public sealed class RestaurantDetailDto
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string? CoverUrl { get; init; }
    public string AddressLine { get; init; } = string.Empty;
    public string? ProvinceName { get; init; }
    public decimal Latitude { get; init; }
    public decimal Longitude { get; init; }
    public short? PriceLevel { get; init; }
    public decimal RatingAvg { get; init; }
    public int RatingCount { get; init; }
    public string OperatingStatus { get; init; } = string.Empty;
    public bool IsOpenNow { get; init; }
    public decimal? DistanceKm { get; init; }
    public string? Description { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Website { get; init; }
    public string? Ward { get; init; }
    public IReadOnlyList<string> Images { get; init; } = Array.Empty<string>();
    public IReadOnlyList<BusinessHourDto> BusinessHours { get; init; } = Array.Empty<BusinessHourDto>();
    public IReadOnlyList<DishCardDto>? Menu { get; init; }
    public ReviewDto? MyReview { get; init; }
}
