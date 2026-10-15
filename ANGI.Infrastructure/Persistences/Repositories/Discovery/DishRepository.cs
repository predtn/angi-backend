using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Discovery;

/// <summary>Reads public dishes for the Discovery endpoints.</summary>
public sealed class DishRepository : IDishRepository
{
    private readonly ANGIContext _context;

    public DishRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public Task<Dish?> GetPublicDetailAsync(int dishId, CancellationToken ct)
    {
        // Same visibility as REST-02: verified and visible; the soft-delete filter hides deleted restaurants.
        return _context.Dishes
            .AsNoTracking()
            .Where(dish => dish.Id == dishId &&
                           dish.Status == DishStatus.Active &&
                           dish.Restaurant.VerificationStatus == RestaurantVerificationStatus.Verified &&
                           dish.Restaurant.ModerationStatus == RestaurantModerationStatus.Visible)
            .Include(dish => dish.CoverMedia)
            .Include(dish => dish.DishTags)
                .ThenInclude(dishTag => dishTag.Tag)
            .Include(dish => dish.Images)
                .ThenInclude(image => image.Media)
            .Include(dish => dish.Restaurant)
                .ThenInclude(restaurant => restaurant.CoverMedia)
            .Include(dish => dish.Restaurant)
                .ThenInclude(restaurant => restaurant.BusinessHours)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetCandidateIdsAsync(
        DishCandidateFilter filter,
        int maxCount,
        CancellationToken ct)
    {
        var query = Candidates();

        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var term = $"%{filter.Query.Trim()}%";
            query = query.Where(dish => EF.Functions.ILike(dish.Name, term) || EF.Functions.ILike(dish.Restaurant.Name, term));
        }

        if (!string.IsNullOrWhiteSpace(filter.Province))
        {
            var term = $"%{filter.Province.Trim()}%";
            query = query.Where(dish => dish.Restaurant.ProvinceName != null && EF.Functions.ILike(dish.Restaurant.ProvinceName, term));
        }

        if (!string.IsNullOrWhiteSpace(filter.District))
        {
            var term = $"%{filter.District.Trim()}%";
            query = query.Where(dish => dish.Restaurant.District != null && EF.Functions.ILike(dish.Restaurant.District, term));
        }

        if (filter.DishForm is { } dishForm)
        {
            query = query.Where(dish => dish.DishForm == dishForm);
        }

        if (filter.MinPrice is { } minPrice)
        {
            query = query.Where(dish => dish.Price >= minPrice);
        }

        if (filter.MaxPrice is { } maxPrice)
        {
            query = query.Where(dish => dish.Price <= maxPrice);
        }

        if (filter.RestaurantId is { } restaurantId)
        {
            query = query.Where(dish => dish.RestaurantId == restaurantId);
        }

        if (filter.ExcludeTagIds.Count > 0)
        {
            var excluded = filter.ExcludeTagIds.ToArray();
            query = query.Where(dish => !dish.DishTags.Any(dishTag => excluded.Contains(dishTag.TagId)));
        }

        if (filter.TagIds.Count > 0)
        {
            var requested = filter.TagIds.Distinct().ToArray();
            var known = await _context.Tags
                .AsNoTracking()
                .Where(tag => requested.Contains(tag.Id))
                .Select(tag => new { tag.Id, tag.TagType })
                .ToListAsync(ct);
            if (known.Count < requested.Length)
            {
                // A tag that does not exist can match no dish.
                return [];
            }

            // OR within a tag type, AND across types: [lunch, dinner, soup] = (lunch or dinner) and soup.
            foreach (var group in known.GroupBy(tag => tag.TagType))
            {
                var ids = group.Select(tag => tag.Id).ToArray();
                query = query.Where(dish => dish.DishTags.Any(dishTag => ids.Contains(dishTag.TagId)));
            }
        }

        if (filter.Area is { } area)
        {
            var (minLatitude, maxLatitude, minLongitude, maxLongitude) = area.BoundingBox();
            query = query.Where(dish => dish.Restaurant.Latitude >= minLatitude &&
                                        dish.Restaurant.Latitude <= maxLatitude &&
                                        dish.Restaurant.Longitude >= minLongitude &&
                                        dish.Restaurant.Longitude <= maxLongitude);
        }

        var ordered = query
            .OrderByDescending(dish => dish.Restaurant.RatingAvg)
            .ThenByDescending(dish => dish.LikeCount)
            .ThenBy(dish => dish.Id);

        if (filter.Area is null)
        {
            return await ordered.Select(dish => dish.Id).Take(maxCount).ToListAsync(ct);
        }

        // The bounding box keeps the rows few; the exact radius is checked here, keeping the order.
        var inBox = await ordered
            .Select(dish => new { dish.Id, dish.Restaurant.Latitude, dish.Restaurant.Longitude })
            .ToListAsync(ct);
        return inBox
            .Where(row => filter.Area.Contains(row.Latitude, row.Longitude))
            .Select(row => row.Id)
            .Take(maxCount)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetTrendingIdsAsync(string? province, DateTime since, int limit, CancellationToken ct)
    {
        var query = Candidates();
        if (!string.IsNullOrWhiteSpace(province))
        {
            var term = $"%{province.Trim()}%";
            query = query.Where(dish => dish.Restaurant.ProvinceName != null && EF.Functions.ILike(dish.Restaurant.ProvinceName, term));
        }

        return await query
            .Select(dish => new
            {
                dish.Id,
                RecentLikes = _context.DishFeedbacks.Count(feedback =>
                    feedback.DishId == dish.Id && feedback.Value == FeedbackValue.Like && feedback.CreatedAt >= since),
                dish.LikeCount,
                dish.Restaurant.RatingAvg
            })
            .OrderByDescending(row => row.RecentLikes)
            .ThenByDescending(row => row.LikeCount)
            .ThenByDescending(row => row.RatingAvg)
            .ThenBy(row => row.Id)
            .Select(row => row.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Dish>> GetCardDishesAsync(IReadOnlyCollection<int> dishIds, CancellationToken ct)
    {
        var ids = dishIds.ToArray();
        return await Candidates()
            .Where(dish => ids.Contains(dish.Id))
            .Include(dish => dish.CoverMedia)
            .Include(dish => dish.Restaurant)
            .Include(dish => dish.DishTags)
                .ThenInclude(dishTag => dishTag.Tag)
            .AsSplitQuery()
            .ToListAsync(ct);
    }

    /// <summary>
    /// Active, available dishes of open restaurants that are verified and visible; the soft-delete filter hides
    /// deleted restaurants (API Design DISH-01, OWN-05).
    /// </summary>
    private IQueryable<Dish> Candidates() =>
        _context.Dishes
            .AsNoTracking()
            .Where(dish => dish.Status == DishStatus.Active &&
                           dish.IsAvailable &&
                           dish.Restaurant.VerificationStatus == RestaurantVerificationStatus.Verified &&
                           dish.Restaurant.ModerationStatus == RestaurantModerationStatus.Visible &&
                           dish.Restaurant.OperatingStatus == RestaurantOperatingStatus.Open);
}
