using ANGI.Application.DTOs.Account;

namespace ANGI.Application.DTOs.Social;

/// <summary>Complete BlogDetail response returned by BLOG-04 and blog-detail endpoints.</summary>
public sealed class BlogDetailDto
{
    public long Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string Excerpt { get; init; } = string.Empty;
    public string? CoverUrl { get; init; }
    public UserSummaryDto Author { get; init; } = new();
    public bool HasRoadmap { get; init; }
    public int LikeCount { get; init; }
    public int CommentCount { get; init; }
    public int ViewCount { get; init; }
    public DateTime PublishedAt { get; init; }
    public string Content { get; init; } = string.Empty;
    public IReadOnlyList<string> Images { get; init; } = [];
    public long? RoadmapId { get; init; }
    public RoadmapSnapshotDto? RoadmapSnapshot { get; init; }
    public bool LikedByMe { get; init; }
    public bool IsMine { get; init; }
    public DateTime UpdatedAt { get; init; }
}
