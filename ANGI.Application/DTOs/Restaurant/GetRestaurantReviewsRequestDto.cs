namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Encapsulates path and query parameters for REST-04 restaurant reviews retrieval.</summary>
public sealed class GetRestaurantReviewsRequestDto
{
    public long RestaurantId { get; init; }
    public short? Rating { get; init; }
    public bool? HasImage { get; init; }
    public string? Sort { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
