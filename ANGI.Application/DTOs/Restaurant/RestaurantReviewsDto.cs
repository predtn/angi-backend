namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Represents a paginated list of reviews for a restaurant with rating statistics (REST-04).</summary>
public sealed class RestaurantReviewsDto
{
    public IReadOnlyList<ReviewDto> Items { get; init; } = Array.Empty<ReviewDto>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public long TotalItems { get; init; }
    public int TotalPages { get; init; }
    public decimal RatingAvg { get; init; }
    public int RatingCount { get; init; }
    public IReadOnlyDictionary<string, int> RatingBreakdown { get; init; } = new Dictionary<string, int>();
}
