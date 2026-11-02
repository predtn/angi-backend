namespace ANGI.Application.DTOs.Moderation;

/// <summary>Request query parameters for MOD-F1 to search, filter and paginate reviews for moderation.</summary>
public sealed class GetModReviewsRequestDto
{
    public string? Q { get; init; }
    public long? RestaurantId { get; init; }
    public int? UserId { get; init; }
    public short? Rating { get; init; }
    public string? Status { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
