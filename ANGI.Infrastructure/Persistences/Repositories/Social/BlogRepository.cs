using ANGI.Application.Common.Interfaces.Repositories.Social;
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

    /// <summary>Adds the complete blog aggregate to the scoped context.</summary>
    public void Add(Blog blog)
    {
        _context.Blogs.Add(blog);
    }
}
