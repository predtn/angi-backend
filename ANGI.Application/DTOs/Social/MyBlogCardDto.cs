namespace ANGI.Application.DTOs.Social;

/// <summary>MyBlogCard of the API Design (BLOG-05): personal blog card with moderation status and note.</summary>
public sealed class MyBlogCardDto : BlogCardDto
{
    /// <summary>Status of the blog: published, hidden, removed.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Reason for moderation if the blog was hidden or removed by a moderator.</summary>
    public string? ModerationNote { get; init; }
}
