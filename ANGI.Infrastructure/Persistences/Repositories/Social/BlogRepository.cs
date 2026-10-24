using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.Common.Models.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Social;

/// <summary>Provides EF Core reads and writes for the Social blog module.</summary>
public sealed class BlogRepository : IBlogRepository
{
    private readonly ANGIContext _context;

    /// <summary>Initializes Social persistence with the scoped application context.</summary>
    public BlogRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Blog>> GetFeaturedAsync(DateTime recentSince, int limit, CancellationToken ct)
    {
        // The soft-delete filter hides deleted blogs and blogs of deleted authors.
        return await _context.Blogs
            .AsNoTracking()
            .Where(blog => blog.Status == BlogStatus.Published)
            .OrderByDescending(blog => blog.PublishedAt >= recentSince)
            .ThenByDescending(blog => blog.LikeCount + blog.CommentCount)
            .ThenByDescending(blog => blog.PublishedAt)
            .ThenByDescending(blog => blog.Id)
            .Take(limit)
            .Include(blog => blog.CoverMedia)
            .Include(blog => blog.Author)
                .ThenInclude(author => author.AvatarMedia)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<Blog> Items, long TotalItems)> GetPublicBlogsAsync(
        string? q,
        int? authorId,
        bool? hasRoadmap,
        string? sort,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        // The soft-delete filter hides deleted blogs and blogs of deleted authors.
        var query = _context.Blogs
            .AsNoTracking()
            .Where(blog => blog.Status == BlogStatus.Published && blog.Author.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            query = query.Where(blog =>
                EF.Functions.ILike(blog.Title, term) ||
                EF.Functions.ILike(blog.Content, term));
        }

        if (authorId.HasValue)
        {
            query = query.Where(blog => blog.AuthorId == authorId.Value);
        }

        if (hasRoadmap.HasValue)
        {
            query = hasRoadmap.Value
                ? query.Where(blog => blog.RoadmapSnapshot != null)
                : query.Where(blog => blog.RoadmapSnapshot == null);
        }

        var totalItems = await query.LongCountAsync(ct);
        if (totalItems == 0)
        {
            return (Array.Empty<Blog>(), 0L);
        }

        query = string.Equals(sort, "popular", StringComparison.OrdinalIgnoreCase)
            ? query
                .OrderByDescending(blog => blog.LikeCount + blog.CommentCount)
                .ThenByDescending(blog => blog.PublishedAt)
                .ThenByDescending(blog => blog.Id)
            : query
                .OrderByDescending(blog => blog.PublishedAt)
                .ThenByDescending(blog => blog.Id);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(blog => blog.CoverMedia)
            .Include(blog => blog.Author)
                .ThenInclude(author => author.AvatarMedia)
            .ToListAsync(ct);

        return (items, totalItems);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<Blog> Items, Dictionary<long, string?> ModerationNotes, long TotalCount)> GetByAuthorAsync(
        int authorId,
        BlogStatus? status,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var query = _context.Blogs
            .AsNoTracking()
            .Where(blog => blog.AuthorId == authorId);

        if (status.HasValue)
        {
            query = query.Where(blog => blog.Status == status.Value);
        }

        var totalItems = await query.LongCountAsync(ct);
        var totalPages = (int)Math.Ceiling((double)totalItems / pageSize);

        if (totalItems == 0 || page > totalPages)
        {
            return (Array.Empty<Blog>(), new Dictionary<long, string?>(), totalItems);
        }

        var skip = (page - 1) * pageSize;

        var items = await query
            .OrderByDescending(blog => blog.PublishedAt)
            .ThenByDescending(blog => blog.Id)
            .Skip(skip)
            .Take(pageSize)
            .Include(blog => blog.CoverMedia)
            .Include(blog => blog.Author)
                .ThenInclude(author => author.AvatarMedia)
            .ToListAsync(ct);

        var moderationNotes = new Dictionary<long, string?>();
        var moderatedBlogIds = items
            .Where(blog => blog.Status != BlogStatus.Published)
            .Select(blog => (long?)blog.Id)
            .ToList();

        if (moderatedBlogIds.Count > 0)
        {
            var auditLogs = await _context.AuditLogs
                .AsNoTracking()
                .Where(log => log.EntityType == AuditEntityTypes.Blog
                    && moderatedBlogIds.Contains(log.EntityId)
                    && (log.Action == AuditActions.BlogHidden || log.Action == AuditActions.BlogRemoved))
                .OrderByDescending(log => log.CreatedAt)
                .ThenByDescending(log => log.Id)
                .ToListAsync(ct);

            foreach (var log in auditLogs)
            {
                if (log.EntityId.HasValue && !moderationNotes.ContainsKey(log.EntityId.Value))
                {
                    string? reason = null;
                    if (!string.IsNullOrWhiteSpace(log.NewValues))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(log.NewValues);
                            foreach (var prop in doc.RootElement.EnumerateObject())
                            {
                                if (string.Equals(prop.Name, "reason", StringComparison.OrdinalIgnoreCase))
                                {
                                    reason = prop.Value.GetString();
                                    break;
                                }
                            }
                        }
                        catch
                        {
                            // Ignore malformed JSON
                        }
                    }
                    moderationNotes[log.EntityId.Value] = reason;
                }
            }
        }

        return (items, moderationNotes, totalItems);
    }

    /// <summary>Loads the author identity and avatar needed by a BlogDetail response.</summary>
    public Task<User?> GetAuthorAsync(int userId, CancellationToken ct) =>
        _context.Users
            .AsNoTracking()
            .Include(user => user.AvatarMedia)
            .FirstOrDefaultAsync(user => user.Id == userId, ct);

    /// <summary>Loads an owned roadmap and every current value copied into its immutable blog snapshot.</summary>
    public Task<RoadmapSnapshotSource?> GetOwnedRoadmapForSnapshotAsync(long roadmapId, int userId, CancellationToken ct) =>
        _context.Roadmaps
            .AsNoTracking()
            .Where(roadmap => roadmap.Id == roadmapId && roadmap.UserId == userId)
            .Select(roadmap => new RoadmapSnapshotSource
            {
                RoadmapId = roadmap.Id,
                Title = roadmap.Title,
                StartDate = roadmap.StartDate,
                NumDays = roadmap.NumDays,
                Days = roadmap.Days
                    .OrderBy(day => day.DayNumber)
                    .Select(day => new RoadmapSnapshotDaySource
                    {
                        DayNumber = day.DayNumber,
                        Items = day.Items
                            .OrderBy(item => item.MealSlot)
                            .ThenBy(item => item.SortOrder)
                            .ThenBy(item => item.Id)
                            .Select(item => new RoadmapSnapshotItemSource
                            {
                                MealSlot = item.MealSlot,
                                DishId = item.DishId,
                                DishName = item.Dish.Name,
                                Price = item.Dish.Price,
                                RestaurantId = item.Dish.RestaurantId,
                                RestaurantName = item.Dish.Restaurant.Name,
                                AddressLine = item.Dish.Restaurant.AddressLine,
                                Ward = item.Dish.Restaurant.Ward,
                                District = item.Dish.Restaurant.District,
                                ProvinceName = item.Dish.Restaurant.ProvinceName
                            })
                            .ToArray()
                    })
                    .ToArray()
            })
            .FirstOrDefaultAsync(ct);

    /// <summary>Checks the active-blog unique index domain for a candidate slug.</summary>
    public Task<bool> SlugExistsAsync(string slug, CancellationToken ct) =>
        _context.Blogs.AsNoTracking().AnyAsync(blog => blog.Slug == slug, ct);

    /// <inheritdoc />
    public async Task<Blog?> GetPublicDetailAsync(long blogId, CancellationToken ct)
    {
        var blog = await _context.Blogs
            .AsNoTracking()
            .Where(b => b.Id == blogId && b.Status == BlogStatus.Published)
            .Include(b => b.Author)
                .ThenInclude(author => author.AvatarMedia)
            .Include(b => b.CoverMedia)
            .Include(b => b.Images)
                .ThenInclude(image => image.Media)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);

        if (blog is not null && blog.RoadmapId is long roadmapId)
        {
            var roadmapExists = await _context.Roadmaps
                .AsNoTracking()
                .AnyAsync(roadmap => roadmap.Id == roadmapId, ct);
            if (!roadmapExists)
            {
                blog.RoadmapId = null;
            }
        }

        return blog;
    }

    /// <inheritdoc />
    public Task<bool> IsLikedByUserAsync(long blogId, int userId, CancellationToken ct) =>
        _context.BlogLikes
            .AsNoTracking()
            .AnyAsync(like => like.BlogId == blogId && like.UserId == userId, ct);

    /// <inheritdoc />
    public Task<bool> ExistsPublishedAsync(long blogId, CancellationToken ct) =>
        _context.Blogs
            .AsNoTracking()
            .AnyAsync(blog => blog.Id == blogId && blog.Status == BlogStatus.Published, ct);

    /// <inheritdoc />
    public async Task<(IReadOnlyList<BlogComment> Items, long TotalItems)> GetVisibleCommentsAsync(
        long blogId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var query = _context.BlogComments
            .AsNoTracking()
            .Where(comment => comment.BlogId == blogId && comment.Status == ContentStatus.Visible);

        var totalItems = await query.LongCountAsync(ct);
        if (totalItems == 0)
        {
            return (Array.Empty<BlogComment>(), 0L);
        }

        var skip = (page - 1) * pageSize;
        var items = await query
            .OrderBy(comment => comment.CreatedAt)
            .ThenBy(comment => comment.Id)
            .Skip(skip)
            .Take(pageSize)
            .Include(comment => comment.User)
                .ThenInclude(user => user.AvatarMedia)
            .ToListAsync(ct);

        return (items, totalItems);
    }

    /// <summary>Adds the complete blog aggregate to the scoped context.</summary>
    public void Add(Blog blog)
    {
        _context.Blogs.Add(blog);
    }
}
