namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Represents an owner's reply to a restaurant review.</summary>
public sealed class ReviewReplyDto
{
    public string Content { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
