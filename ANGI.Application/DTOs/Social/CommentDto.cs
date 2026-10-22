using ANGI.Application.DTOs.Account;

namespace ANGI.Application.DTOs.Social;

/// <summary>Represents a blog comment response (BLOG-10).</summary>
public sealed class CommentDto
{
    public long Id { get; set; }
    public long BlogId { get; set; }
    public UserSummaryDto User { get; set; } = null!;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsMine { get; set; }
}
