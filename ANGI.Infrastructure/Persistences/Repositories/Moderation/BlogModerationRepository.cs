using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Persistences;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Moderation;

/// <summary>Implements blog and comment moderation persistence against the ANGI database context.</summary>
public sealed class BlogModerationRepository(ANGIContext context) : IBlogModerationRepository
{
    /// <inheritdoc />
    public async Task<Paged<ModBlogCardDto>> ListBlogsAsync(GetModBlogsRequestDto request, CancellationToken ct)
    {
        var query = context.Blogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.Q))
        {
            var term = request.Q.Trim().ToLower();
            query = query.Where(b => b.Title.ToLower().Contains(term) || b.Content.ToLower().Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            var status = request.Status switch
            {
                "published" => BlogStatus.Published,
                "hidden" => BlogStatus.Hidden,
                "removed" => BlogStatus.Removed,
                _ => (BlogStatus?)null
            };

            if (status.HasValue)
            {
                query = query.Where(b => b.Status == status.Value);
            }
        }

        if (request.AuthorId.HasValue)
        {
            query = query.Where(b => b.AuthorId == request.AuthorId.Value);
        }

        if (request.HasPendingReportsFilter.HasValue)
        {
            var hasReports = request.HasPendingReportsFilter.Value;
            query = query.Where(b => context.Reports.Any(r => r.TargetType == ReportTargetType.Blog &&
                r.BlogId == b.Id && (r.Status == ReportStatus.Pending || r.Status == ReportStatus.InReview)) == hasReports);
        }

        var totalItems = await query.LongCountAsync(ct);
        var offset = ((long)request.Page - 1) * request.PageSize;

        if (offset > int.MaxValue)
        {
            return new Paged<ModBlogCardDto>
            {
                Items = [],
                Page = request.Page,
                PageSize = request.PageSize,
                TotalItems = totalItems,
                TotalPages = (int)Math.Ceiling((double)totalItems / request.PageSize)
            };
        }

        var rawItems = await query
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Id)
            .Skip((int)offset)
            .Take(request.PageSize)
            .Select(b => new
            {
                Blog = b,
                Author = b.Author,
                AuthorAvatar = b.Author.AvatarMedia != null ? b.Author.AvatarMedia.Url : null,
                CoverUrl = b.CoverMedia != null ? b.CoverMedia.Url : null,
                PendingReportCount = context.Reports.Count(r => r.TargetType == ReportTargetType.Blog &&
                    r.BlogId == b.Id && (r.Status == ReportStatus.Pending || r.Status == ReportStatus.InReview))
            })
            .ToListAsync(ct);

        var items = rawItems.Select(x => new ModBlogCardDto
        {
            Id = x.Blog.Id,
            Title = x.Blog.Title,
            Slug = x.Blog.Slug,
            Excerpt = x.Blog.Content.Length <= 200 ? x.Blog.Content.Trim() : x.Blog.Content[..200].TrimEnd(),
            CoverUrl = x.CoverUrl,
            Author = new UserSummaryDto
            {
                Id = x.Author.Id,
                DisplayName = x.Author.DisplayName,
                AvatarUrl = x.AuthorAvatar
            },
            HasRoadmap = x.Blog.RoadmapSnapshot != null,
            LikeCount = x.Blog.LikeCount,
            CommentCount = x.Blog.CommentCount,
            ViewCount = x.Blog.ViewCount,
            PublishedAt = x.Blog.PublishedAt,
            Status = x.Blog.Status switch
            {
                BlogStatus.Published => "published",
                BlogStatus.Hidden => "hidden",
                _ => "removed"
            },
            PendingReportCount = x.PendingReportCount
        }).ToList();

        return new Paged<ModBlogCardDto>
        {
            Items = items,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalItems = totalItems,
            TotalPages = (int)Math.Ceiling((double)totalItems / request.PageSize)
        };
    }

    /// <inheritdoc />
    public Task<Blog?> GetBlogByIdAsync(long blogId, CancellationToken ct) =>
        context.Blogs.FirstOrDefaultAsync(b => b.Id == blogId, ct);

    /// <inheritdoc />
    public Task<BlogComment?> GetCommentByIdAsync(long commentId, CancellationToken ct) =>
        context.BlogComments.FirstOrDefaultAsync(c => c.Id == commentId, ct);

    /// <inheritdoc />
    public Task AddToCommentCountAsync(long blogId, int delta, CancellationToken ct) =>
        context.Blogs
            .Where(b => b.Id == blogId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.CommentCount, b => Math.Max(b.CommentCount + delta, 0)), ct);

    /// <inheritdoc />
    public void AddNotification(Domain.Entities.Notification notification) =>
        context.Notifications.Add(notification);
}
