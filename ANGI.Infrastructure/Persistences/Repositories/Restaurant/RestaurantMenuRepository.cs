using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Restaurant;

/// <summary>Provides EF Core persistence for owner menu management.</summary>
public sealed class RestaurantMenuRepository : IRestaurantMenuRepository
{
    private readonly ANGIContext _context;

    /// <summary>Initializes menu persistence with the scoped application context.</summary>
    public RestaurantMenuRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <summary>Locks the owner's restaurant row until the surrounding transaction completes.</summary>
    public Task<Domain.Entities.Restaurant?> LockOwnerRestaurantAsync(int ownerId, CancellationToken ct)
    {
        return _context.Restaurants
            .FromSqlInterpolated($$"""
                SELECT *
                FROM core.restaurants
                WHERE owner_id = {{ownerId}} AND deleted_at IS NULL
                FOR UPDATE
                """)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Loads non-removed dishes and the single open submission for the owner menu screen.</summary>
    public Task<Domain.Entities.Restaurant?> GetOwnerMenuAsync(int ownerId, CancellationToken ct)
    {
        return _context.Restaurants
            .AsNoTracking()
            .Include(restaurant => restaurant.Dishes.Where(dish => dish.Status != DishStatus.Removed))
                .ThenInclude(dish => dish.CoverMedia)
            .Include(restaurant => restaurant.Dishes.Where(dish => dish.Status != DishStatus.Removed))
                .ThenInclude(dish => dish.DishTags)
                    .ThenInclude(dishTag => dishTag.Tag)
            .Include(restaurant => restaurant.MenuSubmissions.Where(submission =>
                submission.Status == MenuSubmissionStatus.Draft ||
                submission.Status == MenuSubmissionStatus.Pending))
            .AsSplitQuery()
            .FirstOrDefaultAsync(restaurant => restaurant.OwnerId == ownerId, ct);
    }

    /// <summary>Loads a tracked dish through its owner relationship.</summary>
    public Task<Dish?> GetTrackedOwnerDishAsync(int ownerId, int dishId, CancellationToken ct)
    {
        return _context.Dishes.FirstOrDefaultAsync(
            dish => dish.Id == dishId &&
                    dish.Restaurant.OwnerId == ownerId &&
                    dish.Status != DishStatus.Removed,
            ct);
    }

    /// <summary>Loads the draft or pending submission of a restaurant.</summary>
    public Task<MenuSubmission?> GetOpenSubmissionAsync(long restaurantId, CancellationToken ct)
    {
        return _context.MenuSubmissions.FirstOrDefaultAsync(
            submission => submission.RestaurantId == restaurantId &&
                (submission.Status == MenuSubmissionStatus.Draft ||
                 submission.Status == MenuSubmissionStatus.Pending),
            ct);
    }

    /// <summary>Loads source dishes and tag rows needed to clone the current menu.</summary>
    public async Task<IReadOnlyList<Dish>> GetDraftSourceDishesAsync(
        long restaurantId,
        CancellationToken ct)
    {
        return await _context.Dishes
            .AsNoTracking()
            .Include(dish => dish.DishTags)
            .Include(dish => dish.CoverMedia)
            .Where(dish => dish.RestaurantId == restaurantId && dish.Status != DishStatus.Removed)
            .OrderBy(dish => dish.SortOrder)
            .ThenBy(dish => dish.Id)
            .ToListAsync(ct);
    }

    /// <summary>Adds a menu submission aggregate to the context.</summary>
    public void AddSubmission(MenuSubmission submission)
    {
        _context.MenuSubmissions.Add(submission);
    }

    /// <summary>Loads a read-only filtered page and its total count.</summary>
    public async Task<(IReadOnlyList<MenuSubmission> Items, long TotalItems)> GetSubmissionsAsync(
        int ownerId,
        MenuSubmissionStatus? status,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var query = _context.MenuSubmissions
            .AsNoTracking()
            .Include(submission => submission.Reviewer)
                .ThenInclude(reviewer => reviewer!.AvatarMedia)
            .Where(submission => submission.Restaurant.OwnerId == ownerId);

        if (status.HasValue)
        {
            query = query.Where(submission => submission.Status == status.Value);
        }

        var totalItems = await query.LongCountAsync(ct);
        var items = await query
            .OrderByDescending(submission => submission.CreatedAt)
            .ThenByDescending(submission => submission.Id)
            .Skip(PageOffset.Of(page, pageSize))
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalItems);
    }

    /// <summary>Loads one complete submission and the current restaurant menu without tracking.</summary>
    public Task<MenuSubmission?> GetSubmissionDetailsAsync(
        int ownerId,
        long submissionId,
        CancellationToken ct)
    {
        return SubmissionDetailsQuery(_context.MenuSubmissions.AsNoTracking())
            .FirstOrDefaultAsync(
                submission => submission.Id == submissionId &&
                              submission.Restaurant.OwnerId == ownerId,
                ct);
    }

    /// <summary>Loads one tracked submission with its response navigations.</summary>
    public Task<MenuSubmission?> GetTrackedSubmissionAsync(
        int ownerId,
        long submissionId,
        CancellationToken ct)
    {
        return SubmissionDetailsQuery(_context.MenuSubmissions)
            .FirstOrDefaultAsync(
                submission => submission.Id == submissionId &&
                              submission.Restaurant.OwnerId == ownerId,
                ct);
    }

    /// <summary>Locks an owned submission and loads its items for a serialized state-changing command.</summary>
    public async Task<MenuSubmission?> LockOwnedSubmissionAsync(
        int ownerId,
        long submissionId,
        CancellationToken ct)
    {
        var submission = await _context.MenuSubmissions
            .FromSqlInterpolated($$"""
                SELECT submission.*
                FROM core.menu_submissions AS submission
                INNER JOIN core.restaurants AS restaurant
                    ON restaurant.id = submission.restaurant_id
                WHERE submission.id = {{submissionId}}
                    AND restaurant.owner_id = {{ownerId}}
                    AND restaurant.deleted_at IS NULL
                FOR UPDATE OF submission
                """)
            .FirstOrDefaultAsync(ct);

        if (submission is not null)
        {
            await _context.Entry(submission)
                .Collection(value => value.Items)
                .LoadAsync(ct);
        }

        return submission;
    }

    /// <summary>Loads one tracked item while enforcing submission ownership in the query.</summary>
    public Task<MenuSubmissionItem?> GetTrackedItemAsync(
        int ownerId,
        long submissionId,
        long itemId,
        CancellationToken ct)
    {
        return _context.MenuSubmissionItems
            .Include(item => item.Submission)
            .Include(item => item.SourceDish)
                .ThenInclude(dish => dish!.DishTags)
            .Include(item => item.CoverMedia)
            .FirstOrDefaultAsync(
                item => item.Id == itemId &&
                        item.SubmissionId == submissionId &&
                        item.Submission.Restaurant.OwnerId == ownerId,
                ct);
    }

    /// <summary>Returns only requested tag identifiers that exist in the catalog.</summary>
    public async Task<IReadOnlyList<short>> GetExistingTagIdsAsync(
        IReadOnlyCollection<short> tagIds,
        CancellationToken ct)
    {
        return await _context.Tags
            .AsNoTracking()
            .Where(tag => tagIds.Contains(tag.Id))
            .Select(tag => tag.Id)
            .ToListAsync(ct);
    }

    /// <summary>Adds a proposed item to the context.</summary>
    public void AddItem(MenuSubmissionItem item)
    {
        _context.MenuSubmissionItems.Add(item);
    }

    /// <summary>Marks a proposed item for deletion.</summary>
    public void RemoveItem(MenuSubmissionItem item)
    {
        _context.MenuSubmissionItems.Remove(item);
    }

    /// <summary>Builds the graph shared by submission detail reads and tracked updates.</summary>
    private static IQueryable<MenuSubmission> SubmissionDetailsQuery(IQueryable<MenuSubmission> query)
    {
        return query
            .Include(submission => submission.Reviewer)
                .ThenInclude(reviewer => reviewer!.AvatarMedia)
            .Include(submission => submission.Items)
                .ThenInclude(item => item.CoverMedia)
            .Include(submission => submission.Items)
                .ThenInclude(item => item.SourceDish)
                    .ThenInclude(dish => dish!.DishTags)
            .Include(submission => submission.Restaurant)
                .ThenInclude(restaurant => restaurant.Dishes.Where(dish => dish.Status != DishStatus.Removed))
                    .ThenInclude(dish => dish.CoverMedia)
            .Include(submission => submission.Restaurant)
                .ThenInclude(restaurant => restaurant.Dishes.Where(dish => dish.Status != DishStatus.Removed))
                    .ThenInclude(dish => dish.DishTags)
                        .ThenInclude(dishTag => dishTag.Tag)
            .AsSplitQuery();
    }
}
