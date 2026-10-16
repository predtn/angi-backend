using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Roadmap;

/// <summary>Provides EF Core persistence operations for personal roadmap aggregates.</summary>
public sealed class RoadmapRepository : IRoadmapRepository
{
    private readonly ANGIContext _context;

    /// <summary>Initializes the repository with the scoped application context.</summary>
    public RoadmapRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <summary>Projects a stable, newest-first page of the user's visible roadmap cards.</summary>
    public async Task<Paged<RoadmapSummaryDto>> GetByUserAsync(
        int userId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var query = _context.Roadmaps
            .AsNoTracking()
            .Where(roadmap => roadmap.UserId == userId);
        var totalItems = await query.LongCountAsync(ct);
        var totalPages = (int)Math.Ceiling((double)totalItems / pageSize);

        if (page > totalPages)
        {
            return new Paged<RoadmapSummaryDto>
            {
                Items = Array.Empty<RoadmapSummaryDto>(),
                Page = page,
                PageSize = pageSize,
                TotalItems = totalItems,
                TotalPages = totalPages
            };
        }

        var skip = (page - 1) * pageSize;

        var items = await query
            .OrderByDescending(roadmap => roadmap.CreatedAt)
            .ThenByDescending(roadmap => roadmap.Id)
            .Skip(skip)
            .Take(pageSize)
            .Select(roadmap => new RoadmapSummaryDto
            {
                Id = roadmap.Id,
                Title = roadmap.Title,
                StartDate = roadmap.StartDate,
                EndDate = roadmap.StartDate.HasValue
                    ? roadmap.StartDate.Value.AddDays(roadmap.NumDays - 1)
                    : null,
                NumDays = roadmap.NumDays,
                BudgetAmount = roadmap.BudgetAmount,
                TotalEstimatedCost = roadmap.Days
                    .SelectMany(day => day.Items)
                    .Sum(item => (decimal?)item.Dish.Price) ?? 0m,
                ItemCount = roadmap.Days.SelectMany(day => day.Items).Count(),
                CoverUrl = _context.RoadmapItems
                    .Where(item => item.RoadmapDay.RoadmapId == roadmap.Id)
                    .OrderBy(item => item.RoadmapDay.DayNumber)
                    .ThenBy(item => item.MealSlot == MealSlot.Breakfast ? 0
                        : item.MealSlot == MealSlot.Lunch ? 1
                        : item.MealSlot == MealSlot.Dinner ? 2
                        : item.MealSlot == MealSlot.Snack ? 3
                        : 4)
                    .ThenBy(item => item.SortOrder)
                    .ThenBy(item => item.Id)
                    .Select(item => item.Dish.CoverMedia!.Url)
                    .FirstOrDefault(),
                OriginMode = roadmap.OriginMode == RoadmapOriginMode.Pinned
                    ? "pinned"
                    : roadmap.OriginMode == RoadmapOriginMode.Gps
                        ? "gps"
                        : "none",
                OriginLatitude = roadmap.OriginLatitude,
                OriginLongitude = roadmap.OriginLongitude,
                OriginLabel = roadmap.OriginLabel,
                CreatedAt = roadmap.CreatedAt,
                UpdatedAt = roadmap.UpdatedAt
            })
            .ToArrayAsync(ct);

        return new Paged<RoadmapSummaryDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalItems = totalItems,
            TotalPages = totalPages
        };
    }

    /// <summary>Adds a roadmap and its child days to the current unit of work.</summary>
    public void Add(Domain.Entities.Roadmap roadmap)
    {
        _context.Roadmaps.Add(roadmap);
    }

    /// <summary>Locks the owned roadmap row and loads the graph required for update and response mapping.</summary>
    public async Task<Domain.Entities.Roadmap?> LockAndGetTrackedAsync(
        long roadmapId,
        int userId,
        CancellationToken ct)
    {
        await _context.Database.ExecuteSqlInterpolatedAsync($$"""
            SELECT 1
            FROM core.roadmaps
            WHERE id = {{roadmapId}}
              AND user_id = {{userId}}
              AND deleted_at IS NULL
            FOR UPDATE
            """, ct);

        return await _context.Roadmaps
            .IgnoreQueryFilters()
            .Where(roadmap => roadmap.Id == roadmapId &&
                              roadmap.UserId == userId &&
                              roadmap.DeletedAt == null)
            .Include(roadmap => roadmap.Days)
                .ThenInclude(day => day.Items)
                    .ThenInclude(item => item.Dish)
                        .ThenInclude(dish => dish.CoverMedia)
            .Include(roadmap => roadmap.Days)
                .ThenInclude(day => day.Items)
                    .ThenInclude(item => item.Dish)
                        .ThenInclude(dish => dish.Restaurant)
            .Include(roadmap => roadmap.Days)
                .ThenInclude(day => day.Items)
                    .ThenInclude(item => item.Dish)
                        .ThenInclude(dish => dish.DishTags)
                            .ThenInclude(dishTag => dishTag.Tag)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Projects the current user's feedback for only the requested roadmap items.</summary>
    public async Task<IReadOnlyDictionary<long, FeedbackValue>> GetFeedbackByItemIdsAsync(
        int userId,
        IReadOnlyCollection<long> roadmapItemIds,
        CancellationToken ct)
    {
        if (roadmapItemIds.Count == 0)
        {
            return new Dictionary<long, FeedbackValue>();
        }

        return await _context.DishFeedbacks
            .AsNoTracking()
            .Where(feedback => feedback.UserId == userId &&
                               feedback.RoadmapItemId.HasValue &&
                               roadmapItemIds.Contains(feedback.RoadmapItemId.Value))
            .ToDictionaryAsync(feedback => feedback.RoadmapItemId!.Value, feedback => feedback.Value, ct);
    }
}
