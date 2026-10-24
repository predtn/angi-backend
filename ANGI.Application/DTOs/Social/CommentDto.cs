using ANGI.Application.DTOs.Account;

namespace ANGI.Application.DTOs.Social;

/// <summary>Represents a single-level blog comment returned by BLOG-03 and BLOG-10.</summary>
public sealed class CommentDto
{
    public long Id { get; init; }
    public long BlogId { get; init; }
    public UserSummaryDto User { get; init; } = new();
    public string Content { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public bool IsMine { get; init; }
}
