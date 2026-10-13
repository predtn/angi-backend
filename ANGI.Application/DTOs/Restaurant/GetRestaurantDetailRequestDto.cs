namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Encapsulates path and query parameters for REST-02 restaurant detail retrieval.</summary>
public sealed class GetRestaurantDetailRequestDto
{
    public long RestaurantId { get; init; }
    public decimal? Lat { get; init; }
    public decimal? Lng { get; init; }
    public string? Include { get; init; }
}
