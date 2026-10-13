using ANGI.Application.DTOs.Account;

namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Represents a restaurant review written by a traveler.</summary>
public sealed class ReviewDto
{
    public long Id { get; init; }
    public long RestaurantId { get; init; }
    public UserSummaryDto User { get; init; } = new();
    public short Rating { get; init; }
    public string? Content { get; init; }
    public IReadOnlyList<string> Images { get; init; } = Array.Empty<string>();
    public long? RoadmapItemId { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public ReviewReplyDto? Reply { get; init; }
}
