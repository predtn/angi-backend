using System.Text.Json;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Social;
using ANGI.Domain.Entities;

namespace ANGI.Application.UseCases.Social;

/// <summary>Provides shared entity-to-DTO projections for the Social blog module.</summary>
public static class BlogMapper
{
    public const int ExcerptLength = 200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Maps a loaded <see cref="Blog"/> entity graph to <see cref="BlogDetailDto"/> (BLOG-02).</summary>
    public static BlogDetailDto MapToDetail(Blog blog, bool likedByMe, bool isMine)
    {
        var content = blog.Content ?? string.Empty;
        var excerpt = content.Length <= ExcerptLength
            ? content.Trim()
            : content[..ExcerptLength].TrimEnd();

        var snapshot = string.IsNullOrWhiteSpace(blog.RoadmapSnapshot)
            ? null
            : JsonSerializer.Deserialize<RoadmapSnapshotDto>(blog.RoadmapSnapshot, JsonOptions);

        return new BlogDetailDto
        {
            Id = blog.Id,
            Title = blog.Title,
            Slug = blog.Slug,
            Excerpt = excerpt,
            CoverUrl = blog.CoverMedia?.Url,
            Author = new UserSummaryDto
            {
                Id = blog.Author.Id,
                DisplayName = blog.Author.DisplayName,
                AvatarUrl = blog.Author.AvatarMedia?.Url
            },
            HasRoadmap = snapshot is not null,
            LikeCount = blog.LikeCount,
            CommentCount = blog.CommentCount,
            ViewCount = blog.ViewCount,
            PublishedAt = blog.PublishedAt,
            Content = content,
            Images = blog.Images
                .OrderBy(image => image.SortOrder)
                .ThenBy(image => image.MediaId)
                .Where(image => image.Media is not null)
                .Select(image => image.Media.Url)
                .ToArray(),
            RoadmapId = blog.RoadmapId,
            RoadmapSnapshot = snapshot,
            LikedByMe = likedByMe,
            IsMine = isMine,
            UpdatedAt = blog.UpdatedAt
        };
    }

    /// <summary>Maps a loaded <see cref="BlogComment"/> entity to <see cref="CommentDto"/> (BLOG-03).</summary>
    public static CommentDto MapToComment(BlogComment comment, int? currentUserId) => new()
    {
        Id = comment.Id,
        BlogId = comment.BlogId,
        User = new UserSummaryDto
        {
            Id = comment.User.Id,
            DisplayName = comment.User.DisplayName,
            AvatarUrl = comment.User.AvatarMedia?.Url
        },
        Content = comment.Content,
        CreatedAt = comment.CreatedAt,
        IsMine = currentUserId.HasValue && comment.UserId == currentUserId.Value
    };
}
