using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Moderation;

/// <summary>Provides EF Core persistence for restaurant verification moderation.</summary>
public sealed class VerificationReviewRepository : IVerificationReviewRepository
{
    private readonly ANGIContext _context;

    /// <summary>Initializes the repository with the scoped application context.</summary>
    public VerificationReviewRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <summary>Evaluates role defaults plus allow/deny overrides against the user's current database role.</summary>
    public Task<bool> HasEffectivePermissionAsync(int userId, string permissionCode, CancellationToken ct)
    {
        return _context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId && user.Role.Code == "MOD")
            .Select(user =>
                (!_context.UserPermissions.Any(item =>
                    item.UserId == user.Id &&
                    item.Permission.Code == permissionCode &&
                    item.Effect == PermissionEffect.Deny) &&
                 (_context.UserPermissions.Any(item =>
                      item.UserId == user.Id &&
                      item.Permission.Code == permissionCode &&
                      item.Effect == PermissionEffect.Allow) ||
                  user.Role.RolePermissions.Any(item => item.Permission.Code == permissionCode))))
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>Loads one queue page with signed-document inputs and restaurant card navigations.</summary>
    public async Task<(IReadOnlyList<RestaurantVerification> Items, long TotalItems)> ListAsync(
        ListVerificationsRequestDto request,
        CancellationToken ct)
    {
        var status = request.Status switch
        {
            "approved" => VerificationStatus.Approved,
            "rejected" => VerificationStatus.Rejected,
            "needs_more_info" => VerificationStatus.NeedsMoreInfo,
            _ => VerificationStatus.Pending
        };
        var query = _context.RestaurantVerifications
            .AsNoTracking()
            .Where(verification => verification.Status == status);

        if (!string.IsNullOrWhiteSpace(request.Province))
        {
            var province = $"%{request.Province}%";
            query = query.Where(verification =>
                verification.Restaurant.ProvinceName != null &&
                EF.Functions.ILike(verification.Restaurant.ProvinceName, province));
        }

        var totalItems = await query.LongCountAsync(ct);
        var items = await query
            .OrderBy(verification => verification.SubmittedAt)
            .ThenBy(verification => verification.Id)
            .Skip(PageOffset.Of(request.Page, request.PageSize))
            .Take(request.PageSize)
            .Include(verification => verification.Documents)
                .ThenInclude(document => document.Media)
            .Include(verification => verification.Reviewer)
                .ThenInclude(reviewer => reviewer!.AvatarMedia)
            .Include(verification => verification.Restaurant)
                .ThenInclude(restaurant => restaurant.CoverMedia)
            .Include(verification => verification.Restaurant)
                .ThenInclude(restaurant => restaurant.BusinessHours)
            .AsSplitQuery()
            .ToListAsync(ct);

        return (items, totalItems);
    }

    /// <summary>Loads the complete read-only graph used by the flattened MOD-V2 response.</summary>
    public Task<RestaurantVerification?> GetDetailAsync(long verificationId, CancellationToken ct)
    {
        return _context.RestaurantVerifications
            .AsNoTrackingWithIdentityResolution()
            .Where(verification => verification.Id == verificationId)
            .Include(verification => verification.Documents)
                .ThenInclude(document => document.Media)
            .Include(verification => verification.Reviewer)
                .ThenInclude(reviewer => reviewer!.AvatarMedia)
            .Include(verification => verification.Previous)
                .ThenInclude(previous => previous!.Documents)
                    .ThenInclude(document => document.Media)
            .Include(verification => verification.Previous)
                .ThenInclude(previous => previous!.Reviewer)
                    .ThenInclude(reviewer => reviewer!.AvatarMedia)
            .Include(verification => verification.Restaurant)
                .ThenInclude(restaurant => restaurant.CoverMedia)
            .Include(verification => verification.Restaurant)
                .ThenInclude(restaurant => restaurant.Images)
                    .ThenInclude(image => image.Media)
            .Include(verification => verification.Restaurant)
                .ThenInclude(restaurant => restaurant.BusinessHours)
            .Include(verification => verification.Restaurant)
                .ThenInclude(restaurant => restaurant.Verifications)
                    .ThenInclude(item => item.Documents)
                        .ThenInclude(document => document.Media)
            .Include(verification => verification.Restaurant)
                .ThenInclude(restaurant => restaurant.Verifications)
                    .ThenInclude(item => item.Reviewer)
                        .ThenInclude(reviewer => reviewer!.AvatarMedia)
            .Include(verification => verification.Restaurant)
                .ThenInclude(restaurant => restaurant.MenuSubmissions)
            .AsSplitQuery()
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>Acquires a row lock and explicitly loads only data needed to decide and map MOD-V3.</summary>
    public async Task<RestaurantVerification?> LockForDecisionAsync(long verificationId, CancellationToken ct)
    {
        var verification = await _context.RestaurantVerifications
            .FromSqlInterpolated($$"""
                SELECT *
                FROM core.restaurant_verifications
                WHERE id = {{verificationId}}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(ct);
        if (verification is null)
        {
            return null;
        }

        await _context.Entry(verification).Reference(item => item.Restaurant).LoadAsync(ct);
        await _context.Entry(verification).Collection(item => item.Documents)
            .Query()
            .Include(document => document.Media)
            .LoadAsync(ct);
        return verification;
    }

    /// <summary>Refreshes the reviewer navigation after MOD-V3 assigns its foreign key.</summary>
    public async Task LoadReviewerAsync(RestaurantVerification verification, CancellationToken ct)
    {
        var reference = _context.Entry(verification).Reference(item => item.Reviewer);
        reference.IsLoaded = false;
        await reference.Query().Include(reviewer => reviewer!.AvatarMedia).LoadAsync(ct);
    }

    /// <summary>Adds an owner notification without saving independently.</summary>
    public void AddNotification(Domain.Entities.Notification notification)
    {
        _context.Notifications.Add(notification);
    }
}
