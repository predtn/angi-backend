using ANGI.Application.DTOs.Account;

namespace ANGI.Application.DTOs.Social;

/// <summary>Chi tiết bài viết blog (BLOG-04 response).</summary>
public sealed class BlogDetailDto
{
    public long Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;

    /// <summary>200 ký tự đầu của content (không lưu DB).</summary>
    public string Excerpt { get; init; } = string.Empty;

    public string? CoverUrl { get; init; }
    public UserSummaryDto Author { get; init; } = new();

    /// <summary>blogs.roadmap_snapshot khác NULL.</summary>
    public bool HasRoadmap { get; init; }

    public int LikeCount { get; init; }
    public int CommentCount { get; init; }
    public int ViewCount { get; init; }
    public DateTime PublishedAt { get; init; }

    /// <summary>Nội dung bài viết (HTML đã sanitize).</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Danh sách URL ảnh đính kèm trong bài.</summary>
    public List<string> Images { get; init; } = [];

    public long? RoadmapId { get; init; }
    public RoadmapSnapshotDto? RoadmapSnapshot { get; init; }
    public bool LikedByMe { get; init; }
    public bool IsMine { get; init; }
    public DateTime UpdatedAt { get; init; }
}
