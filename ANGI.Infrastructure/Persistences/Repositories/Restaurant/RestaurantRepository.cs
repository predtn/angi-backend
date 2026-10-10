using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Domain.Enums;
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

    /// <summary>Loads a read-only owner aggregate with media, hours, and active workflow summaries.</summary>
    public Task<Domain.Entities.Restaurant?> GetOwnerProfileAsync(int ownerId, CancellationToken ct)
    {
        return OwnerProfileQuery(_context.Restaurants.AsNoTracking())
            .FirstOrDefaultAsync(restaurant => restaurant.OwnerId == ownerId, ct);
    }

    /// <summary>Loads the owner aggregate with tracking for profile and gallery replacement.</summary>
    public Task<Domain.Entities.Restaurant?> GetTrackedOwnerProfileAsync(int ownerId, CancellationToken ct)
    {
        return OwnerProfileQuery(_context.Restaurants)
            .FirstOrDefaultAsync(restaurant => restaurant.OwnerId == ownerId, ct);
    }

    /// <summary>Builds the common complete owner-profile query used by reads and updates.</summary>
    private static IQueryable<Domain.Entities.Restaurant> OwnerProfileQuery(
        IQueryable<Domain.Entities.Restaurant> query)
    {
        return query
            .Include(restaurant => restaurant.CoverMedia)
            .Include(restaurant => restaurant.Images)
                .ThenInclude(image => image.Media)
            .Include(restaurant => restaurant.BusinessHours)
            .Include(restaurant => restaurant.Verifications.OrderByDescending(item => item.SubmittedAt).Take(1))
                .ThenInclude(verification => verification.Documents)
                    .ThenInclude(document => document.Media)
            .Include(restaurant => restaurant.Verifications.OrderByDescending(item => item.SubmittedAt).Take(1))
                .ThenInclude(verification => verification.Reviewer)
                    .ThenInclude(reviewer => reviewer!.AvatarMedia)
            .Include(restaurant => restaurant.MenuSubmissions.Where(submission =>
                submission.Status == MenuSubmissionStatus.Draft ||
                submission.Status == MenuSubmissionStatus.Pending))
            .AsSplitQuery();
    }
}
