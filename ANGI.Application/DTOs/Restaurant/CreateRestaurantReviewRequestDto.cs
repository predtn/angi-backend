namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Request body for submitting a restaurant review (REV-01).</summary>
public sealed class CreateRestaurantReviewRequestDto
{
    public short Rating { get; init; }
    public string? Content { get; init; }
    public IReadOnlyList<long>? ImageMediaIds { get; init; }
    public long? RoadmapItemId { get; init; }
}
