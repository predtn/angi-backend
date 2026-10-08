using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Restaurant;

/// <summary>Provides EF Core persistence operations for restaurant aggregates.</summary>
public sealed class RestaurantRepository : IRestaurantRepository
{
    private readonly ANGIContext _context;

    /// <summary>Initializes the repository with the scoped application context.</summary>
    public RestaurantRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <summary>Acquires a row lock held until the surrounding transaction completes.</summary>
    public async Task LockOwnerForRegistrationAsync(int ownerId, CancellationToken ct)
    {
        await _context.Users
            .FromSqlInterpolated($$"""
                SELECT *
                FROM core.users
                WHERE id = {{ownerId}}
                FOR UPDATE
                """)
            .IgnoreQueryFilters()
            .SingleAsync(ct);
    }

    /// <summary>Checks the filtered restaurant set for an existing owner record.</summary>
    public Task<bool> ExistsByOwnerIdAsync(int ownerId, CancellationToken ct)
    {
        return _context.Restaurants
            .AsNoTracking()
            .AnyAsync(restaurant => restaurant.OwnerId == ownerId, ct);
    }

    /// <summary>Checks the filtered restaurant set for a slug collision.</summary>
    public Task<bool> SlugExistsAsync(string slug, CancellationToken ct)
    {
        return _context.Restaurants
            .AsNoTracking()
            .AnyAsync(restaurant => restaurant.Slug == slug, ct);
    }

    /// <summary>Adds a complete restaurant aggregate without saving immediately.</summary>
    public void Add(Domain.Entities.Restaurant restaurant)
    {
        _context.Restaurants.Add(restaurant);
    }
}
