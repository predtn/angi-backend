using System.Linq.Expressions;
using ANGI.Application.Common.Models;
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
        if (await FilteredAsync(filter, ct) is not { } query) return [];
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
    public async Task<int> CountCandidatesAsync(DishCandidateFilter filter, CancellationToken ct)
    {
        if (await FilteredAsync(filter, ct) is not { } query) return 0;
        if (filter.Area is null) return await query.CountAsync(ct);
        // The exact radius is checked on the rows of the bounding box, as the candidate search does.
        var inBox = await query.Select(dish => new { dish.Restaurant.Latitude, dish.Restaurant.Longitude }).ToListAsync(ct);
        return inBox.Count(row => filter.Area.Contains(row.Latitude, row.Longitude));
    }

    /// <summary>The candidate query with every condition of the filter, the radius as a bounding box; null when a requested tag does not exist.</summary>
    private async Task<IQueryable<Dish>?> FilteredAsync(DishCandidateFilter filter, CancellationToken ct)
    {
        var query = Candidates();
        if (filter.DishIds.Count > 0)
        {
            var includedDishes = filter.DishIds.ToArray();
            query = query.Where(dish => includedDishes.Contains(dish.Id));
        }

        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var term = LikePattern.Contains(filter.Query);
            query = query.Where(dish => EF.Functions.ILike(dish.Name, term, LikePattern.Escape) ||
                                        EF.Functions.ILike(dish.Restaurant.Name, term, LikePattern.Escape));
        }

        if (!string.IsNullOrWhiteSpace(filter.Province))
        {
            var term = ProvinceSearch.Pattern(filter.Province);
            query = query.Where(dish => dish.Restaurant.ProvinceName != null && EF.Functions.ILike(dish.Restaurant.ProvinceName, term, LikePattern.Escape));
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

        if (filter.AnyTagIds.Count > 0)
        {
            var any = filter.AnyTagIds.ToArray();
            query = query.Where(dish => dish.DishTags.Any(dishTag => any.Contains(dishTag.TagId)));
        }

        if (!string.IsNullOrWhiteSpace(filter.MealTimeCode))
        {
            var code = filter.MealTimeCode;
            query = query.Where(dish => dish.DishTags.Any(dishTag =>
                dishTag.Tag.TagType == TagType.MealTime && dishTag.Tag.Code == code));
        }
        if (!string.IsNullOrWhiteSpace(filter.CompatibleMealTimeCode))
        {
            var code = filter.CompatibleMealTimeCode;
            query = query.Where(dish => !dish.DishTags.Any(tag => tag.Tag.TagType == TagType.MealTime) ||
                dish.DishTags.Any(tag => tag.Tag.TagType == TagType.MealTime && tag.Tag.Code == code));
        }

        IEnumerable<short?> days = filter.OpenOnDays.Count > 0 ? filter.OpenOnDays.Distinct().Select(day => (short?)day) : [null];
        IEnumerable<(MealHours.Window Window, short? Day)> hourChecks = filter.OpenAtTime is { } moment
            ? days.SelectMany(day => MealHours.StaysOpen(moment, day))
            : MealHours.For(filter.OpenForMeal) is { } mealWindow ? days.Select(day => (mealWindow, day)) : [];
        foreach (var (window, day) in hourChecks) query = query.Where(OpenDuring(window, day));

        if (filter.ExcludeDishIds.Count > 0)
        {
            var excludedDishes = filter.ExcludeDishIds.ToArray();
            query = query.Where(dish => !excludedDishes.Contains(dish.Id));
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
                return null;
            }

            // OR within a tag type, AND across types: [lunch, dinner, soup] = (lunch or dinner) and soup.
            var groups = filter.RequireAllTags ? known.Select(tag => new[] { tag.Id }) :
                known.GroupBy(tag => tag.TagType).Select(group => group.Select(tag => tag.Id).ToArray());
            foreach (var ids in groups)
            {
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

        return query;
    }

    /// <inheritdoc />
    public void AddRoll(DishRoll roll) => _context.DishRolls.Add(roll);

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetTrendingIdsAsync(string? province, DateTime since, int limit, CancellationToken ct)
    {
        var query = Candidates();
        if (!string.IsNullOrWhiteSpace(province))
        {
            var term = ProvinceSearch.Pattern(province);
            query = query.Where(dish => dish.Restaurant.ProvinceName != null && EF.Functions.ILike(dish.Restaurant.ProvinceName, term, LikePattern.Escape));
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
                .ThenInclude(restaurant => restaurant.BusinessHours)
            .Include(dish => dish.DishTags)
                .ThenInclude(dishTag => dishTag.Tag)
            .AsSplitQuery()
            .ToListAsync(ct);
    }

    /// <summary>dish =&gt; !dish.Restaurant.BusinessHours.Any() || dish.Restaurant.BusinessHours.Any(MealHours.Overlaps(window, day)), built so SQL runs the shared rule.</summary>
    private static Expression<Func<Dish, bool>> OpenDuring(MealHours.Window window, short? day)
    {
        var dish = Expression.Parameter(typeof(Dish), "dish");
        var hours = Expression.Property(Expression.Property(dish, nameof(Dish.Restaurant)), nameof(Dish.Restaurant.BusinessHours));
        var none = Expression.Not(Expression.Call(typeof(Enumerable), nameof(Enumerable.Any), [typeof(RestaurantBusinessHour)], hours));
        var reaches = Expression.Call(typeof(Enumerable), nameof(Enumerable.Any), [typeof(RestaurantBusinessHour)], hours, MealHours.Overlaps(window, day));
        return Expression.Lambda<Func<Dish, bool>>(Expression.OrElse(none, reaches), dish);
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
