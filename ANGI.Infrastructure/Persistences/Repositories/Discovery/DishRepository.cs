using ANGI.Application.Common.Interfaces.Repositories.Discovery;
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
}
