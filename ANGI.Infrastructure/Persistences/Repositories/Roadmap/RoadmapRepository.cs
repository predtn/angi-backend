using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Domain.Entities;
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

        var skip = PageOffset.Of(page, pageSize);

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

    /// <summary>Locks and tracks only the owned visible roadmap row needed by RM-05.</summary>
    public async Task<Domain.Entities.Roadmap?> LockAndGetTrackedForDeleteAsync(
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
            .FirstOrDefaultAsync(
                roadmap => roadmap.Id == roadmapId &&
                           roadmap.UserId == userId &&
                           roadmap.DeletedAt == null,
                ct);
    }

    /// <summary>Loads the complete owned roadmap graph without tracking for RM-03 mapping.</summary>
    public async Task<Domain.Entities.Roadmap?> GetOwnedDetailAsync(
        long roadmapId,
        int userId,
        CancellationToken ct)
    {
        return await _context.Roadmaps
            .AsNoTracking()
            .Where(roadmap => roadmap.Id == roadmapId && roadmap.UserId == userId)
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

    /// <summary>Loads one owned RM-12 item with the full dish and restaurant graph without tracking.</summary>
    public Task<RoadmapItem?> GetOwnedItemDetailAsync(
        long roadmapId,
        long itemId,
        int userId,
        CancellationToken ct)
    {
        return _context.RoadmapItems
            .AsNoTracking()
            .Where(item => item.Id == itemId &&
                           item.RoadmapDay.RoadmapId == roadmapId &&
                           item.RoadmapDay.Roadmap.UserId == userId)
            .Include(item => item.RoadmapDay)
            .Include(item => item.Dish)
                .ThenInclude(dish => dish.CoverMedia)
            .Include(item => item.Dish)
                .ThenInclude(dish => dish.Images)
                    .ThenInclude(image => image.Media)
            .Include(item => item.Dish)
                .ThenInclude(dish => dish.DishTags)
                    .ThenInclude(dishTag => dishTag.Tag)
            .Include(item => item.Dish)
                .ThenInclude(dish => dish.Restaurant)
                    .ThenInclude(restaurant => restaurant.CoverMedia)
            .Include(item => item.Dish)
                .ThenInclude(dish => dish.Restaurant)
                    .ThenInclude(restaurant => restaurant.Images)
                        .ThenInclude(image => image.Media)
            .Include(item => item.Dish)
                .ThenInclude(dish => dish.Restaurant)
                    .ThenInclude(restaurant => restaurant.BusinessHours)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Projects an owned roadmap and only the requested day's route values without tracking.</summary>
    public async Task<RoadmapRouteData?> GetOwnedRouteDataAsync(
        long roadmapId,
        long dayId,
        int userId,
        CancellationToken ct)
    {
        return await _context.Roadmaps
            .AsNoTracking()
            .Where(roadmap => roadmap.Id == roadmapId && roadmap.UserId == userId)
            .Select(roadmap => new RoadmapRouteData
            {
                OriginMode = roadmap.OriginMode,
                OriginLatitude = roadmap.OriginLatitude,
                OriginLongitude = roadmap.OriginLongitude,
                OriginLabel = roadmap.OriginLabel,
                Day = roadmap.Days
                    .Where(day => day.Id == dayId)
                    .Select(day => new RoadmapDayRouteData
                    {
                        Id = day.Id,
                        Items = day.Items.Select(item => new RoadmapRouteItemData
                        {
                            Id = item.Id,
                            MealSlot = item.MealSlot,
                            SortOrder = item.SortOrder,
                            Latitude = item.Dish.Restaurant.Latitude,
                            Longitude = item.Dish.Restaurant.Longitude
                        }).ToArray()
                    })
                    .FirstOrDefault()
            })
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

    /// <summary>Projects only the owned roadmap origin and dish exclusions needed by RM-06.</summary>
    public Task<RoadmapSuggestionContext?> GetSuggestionContextAsync(
        long roadmapId,
        int userId,
        CancellationToken ct)
    {
        return _context.Roadmaps
            .AsNoTracking()
            .Where(roadmap => roadmap.Id == roadmapId && roadmap.UserId == userId)
            .Select(roadmap => new RoadmapSuggestionContext(
                roadmap.OriginMode,
                roadmap.OriginLatitude,
                roadmap.OriginLongitude,
                roadmap.Days.SelectMany(day => day.Items).Select(item => item.DishId).Distinct().ToArray()))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Locks an owned roadmap before loading and tracking the requested day and its item orders.</summary>
    public async Task<(bool RoadmapExists, RoadmapDay? Day)> LockAndGetOwnedDayAsync(
        long roadmapId,
        long dayId,
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

        var roadmapExists = await _context.Roadmaps
            .AsNoTracking()
            .AnyAsync(roadmap => roadmap.Id == roadmapId && roadmap.UserId == userId, ct);
        if (!roadmapExists)
        {
            return (false, null);
        }

        var day = await _context.RoadmapDays
            .Where(value => value.Id == dayId && value.RoadmapId == roadmapId)
            .Include(value => value.Items)
            .FirstOrDefaultAsync(ct);
        return (true, day);
    }

    /// <summary>Locks the owned roadmap row and tracks one RM-08 item with the graph required by RoadmapItem.</summary>
    public async Task<RoadmapItem?> LockAndGetOwnedItemAsync(
        long roadmapId,
        long itemId,
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

        return await _context.RoadmapItems
            .Where(item => item.Id == itemId &&
                           item.RoadmapDay.RoadmapId == roadmapId &&
                           item.RoadmapDay.Roadmap.UserId == userId)
            .Include(item => item.RoadmapDay)
            .Include(item => item.Dish)
                .ThenInclude(dish => dish.CoverMedia)
            .Include(item => item.Dish)
                .ThenInclude(dish => dish.Restaurant)
            .Include(item => item.Dish)
                .ThenInclude(dish => dish.DishTags)
                    .ThenInclude(dishTag => dishTag.Tag)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Tracks an RM-08 destination day after constraining it to the current user's roadmap.</summary>
    public Task<RoadmapDay?> GetOwnedDayAsync(long roadmapId, long dayId, int userId, CancellationToken ct)
    {
        return _context.RoadmapDays
            .Where(day => day.Id == dayId &&
                          day.RoadmapId == roadmapId &&
                          day.Roadmap.UserId == userId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Adds one RM-07 item to the current unit of work.</summary>
    public void AddItem(RoadmapItem item)
    {
        _context.RoadmapItems.Add(item);
    }

    /// <summary>Loads and row-locks an RM-09 item after checking route membership, ownership, and visibility.</summary>
    public Task<RoadmapItem?> LockAndGetOwnedItemForDeleteAsync(
        long roadmapId,
        long itemId,
        int userId,
        CancellationToken ct) =>
        LockAndGetOwnedItemCoreAsync(roadmapId, itemId, userId, ct);

    /// <inheritdoc />
    public Task<RoadmapItem?> LockAndGetOwnedItemForFeedbackAsync(
        long roadmapId,
        long itemId,
        int userId,
        CancellationToken ct) =>
        LockAndGetOwnedItemCoreAsync(roadmapId, itemId, userId, ct);

    /// <summary>Row-locks an item after checking route membership, ownership, and visibility, then tracks it.</summary>
    private async Task<RoadmapItem?> LockAndGetOwnedItemCoreAsync(
        long roadmapId,
        long itemId,
        int userId,
        CancellationToken ct)
    {
        await _context.Database.ExecuteSqlInterpolatedAsync($$"""
            SELECT 1
            FROM core.roadmap_items AS item
            INNER JOIN core.roadmap_days AS day ON day.id = item.roadmap_day_id
            INNER JOIN core.roadmaps AS roadmap ON roadmap.id = day.roadmap_id
            WHERE item.id = {{itemId}}
              AND roadmap.id = {{roadmapId}}
              AND roadmap.user_id = {{userId}}
              AND roadmap.deleted_at IS NULL
            FOR UPDATE OF item
            """, ct);

        return await _context.RoadmapItems.FirstOrDefaultAsync(
            item => item.Id == itemId &&
                    item.RoadmapDay.RoadmapId == roadmapId &&
                    item.RoadmapDay.Roadmap.UserId == userId &&
                    item.RoadmapDay.Roadmap.DeletedAt == null,
            ct);
    }

    /// <summary>Marks a tracked item (the old RM-08 item, or the RM-09 item) for hard deletion in the current unit of work.</summary>
    public void RemoveItem(RoadmapItem item)
    {
        _context.RoadmapItems.Remove(item);
    }

    /// <inheritdoc />
    public Task<bool> ItemHasFeedbackAsync(long roadmapItemId, CancellationToken ct) =>
        _context.DishFeedbacks.AnyAsync(feedback => feedback.RoadmapItemId == roadmapItemId, ct);

    /// <inheritdoc />
    public void AddFeedback(DishFeedback feedback)
    {
        _context.DishFeedbacks.Add(feedback);
    }

    /// <inheritdoc />
    public async Task<(int LikeCount, int DislikeCount)> IncrementDishFeedbackCountAsync(
        int dishId,
        FeedbackValue value,
        CancellationToken ct)
    {
        // One UPDATE with the increment in SQL, so concurrent feedback on the same dish never loses a count.
        var dish = _context.Dishes.Where(item => item.Id == dishId);
        if (value == FeedbackValue.Like)
        {
            await dish.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LikeCount, item => item.LikeCount + 1), ct);
        }
        else
        {
            await dish.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DislikeCount, item => item.DislikeCount + 1), ct);
        }

        var counts = await dish
            .AsNoTracking()
            .Select(item => new { item.LikeCount, item.DislikeCount })
            .SingleAsync(ct);
        return (counts.LikeCount, counts.DislikeCount);
    }
}
