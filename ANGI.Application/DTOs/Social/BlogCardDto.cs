using ANGI.Application.DTOs.Account;

namespace ANGI.Application.DTOs.Social;

/// <summary>BlogCard of the API Design: a blog in a list.</summary>
public class BlogCardDto
{
    public long Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;

    /// <summary>The first 200 characters of the content (not stored).</summary>
    public string Excerpt { get; init; } = string.Empty;

    public string? CoverUrl { get; init; }
    public UserSummaryDto Author { get; init; } = new();

    /// <summary>blogs.roadmap_snapshot is not null.</summary>
    public bool HasRoadmap { get; init; }

    public int LikeCount { get; init; }
    public int CommentCount { get; init; }
    public int ViewCount { get; init; }
    public DateTime PublishedAt { get; init; }
}
