namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Encapsulates query parameters for REST-01 restaurant browsing.</summary>
public sealed class BrowseRestaurantsRequestDto
{
    public string? Q { get; init; }
    public string? Province { get; init; }
    public short? PriceLevel { get; init; }
    public decimal? MinRating { get; init; }
    public bool? OpenNow { get; init; }
    public decimal? Lat { get; init; }
    public decimal? Lng { get; init; }
    public decimal? RadiusKm { get; init; }
    public string? Sort { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
