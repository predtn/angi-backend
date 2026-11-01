using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Persistences;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Moderation;

/// <summary>Queries and aggregates user data for moderator review.</summary>
public sealed class UserModerationRepository(ANGIContext context, TimeProvider clock) : IUserModerationRepository
{
    /// <summary>Lists non-staff users using the documented filters and pagination.</summary>
    public async Task<Paged<AdminUserCardDto>> ListAsync(ListUsersRequestDto request, CancellationToken ct)
    {
        var query = context.Users.AsNoTracking()
            .Where(u => u.DeletedAt == null && u.Role.Code != "MOD" && u.Role.Code != "ADMIN");

        if (!string.IsNullOrWhiteSpace(request.Q))
        {
            var term = request.Q.Trim().ToLower();
            query = query.Where(u => u.Email.ToLower().Contains(term) || u.DisplayName.ToLower().Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(request.Role))
        {
            var roleCode = request.Role.Trim().ToUpperInvariant();
            query = query.Where(u => u.Role.Code == roleCode);
        }

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (TryParseStatus(request.Status, out var status))
            {
                query = query.Where(u => u.Status == status);
            }
        }

        if (request.HasPendingReports.HasValue)
        {
            var hasReports = request.HasPendingReports.Value;
            query = query.Where(u =>
                (context.Reports.Any(r =>
                    (r.Status == ReportStatus.Pending || r.Status == ReportStatus.InReview) &&
                    ((r.TargetType == ReportTargetType.Blog && context.Blogs.Any(b => b.Id == r.BlogId && b.AuthorId == u.Id)) ||
                     (r.TargetType == ReportTargetType.Restaurant && context.Restaurants.Any(rst => rst.Id == r.RestaurantId && rst.OwnerId == u.Id))))) == hasReports);
        }

        var count = await query.LongCountAsync(ct);
        var offset = ((long)request.Page - 1) * request.PageSize;
        var totalPages = (int)Math.Ceiling((double)count / request.PageSize);

        if (count == 0 || offset > int.MaxValue)
        {
            return new Paged<AdminUserCardDto>
            {
                Items = [],
                Page = request.Page,
                PageSize = request.PageSize,
                TotalItems = count,
                TotalPages = totalPages
            };
        }

        var records = await query.OrderByDescending(u => u.CreatedAt).ThenByDescending(u => u.Id)
            .Skip((int)offset).Take(request.PageSize)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.DisplayName,
                AvatarUrl = u.AvatarMedia != null ? u.AvatarMedia.Url : null,
                Role = u.Role.Code,
                u.Status,
                u.SuspendedUntil,
                u.LastLoginAt,
                u.CreatedAt
            })
            .ToListAsync(ct);

        var items = records.Select(u => new AdminUserCardDto
        {
            Id = u.Id,
            Email = u.Email,
            DisplayName = u.DisplayName,
            AvatarUrl = u.AvatarUrl,
            Role = u.Role,
            Status = ToSnakeCase(u.Status),
            SuspendedUntil = u.SuspendedUntil,
            LastLoginAt = u.LastLoginAt,
            CreatedAt = u.CreatedAt
        }).ToArray();

        return new Paged<AdminUserCardDto>
        {
            Items = items,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalItems = count,
            TotalPages = totalPages
        };
    }

    /// <summary>Gathers complete user moderation detail including sanctions, content counts and owned restaurant.</summary>
    public async Task<ModUserDetailDto?> GetDetailAsync(int userId, CancellationToken ct)
    {
        var user = await context.Users.AsNoTracking()
            .Include(u => u.Role)
            .Include(u => u.AvatarMedia)
            .FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null && u.Role.Code != "MOD" && u.Role.Code != "ADMIN", ct);

        if (user is null)
        {
            return null;
        }

        var sanctions = await context.UserSanctions.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.StartsAt)
            .ThenByDescending(s => s.Id)
            .ToListAsync(ct);

        var modIds = sanctions.Select(s => s.IssuedBy)
            .Concat(sanctions.Where(s => s.LiftedBy.HasValue).Select(s => s.LiftedBy!.Value))
            .Distinct()
            .ToList();

        var modUsers = modIds.Count == 0
            ? new Dictionary<int, UserSummaryDto>()
            : await context.Users.AsNoTracking()
                .Where(u => modIds.Contains(u.Id))
                .Select(u => new UserSummaryDto
                {
                    Id = u.Id,
                    DisplayName = u.DisplayName,
                    AvatarUrl = u.AvatarMedia != null ? u.AvatarMedia.Url : null
                })
                .ToDictionaryAsync(u => u.Id, ct);

        var sanctionDtos = sanctions.Select(s => new SanctionDto
        {
            Id = s.Id,
            Type = ToSnakeCase(s.Type),
            Reason = s.Reason,
            ReportId = s.ReportId,
            IssuedBy = modUsers.TryGetValue(s.IssuedBy, out var issuer) ? issuer : new UserSummaryDto { Id = s.IssuedBy },
            StartsAt = s.StartsAt,
            EndsAt = s.EndsAt,
            LiftedAt = s.LiftedAt,
            LiftedBy = s.LiftedBy.HasValue && modUsers.TryGetValue(s.LiftedBy.Value, out var lifter) ? lifter : null,
            LiftReason = s.LiftReason
        }).ToArray();

        var blogCount = await context.Blogs.AsNoTracking()
            .CountAsync(b => b.AuthorId == userId && b.DeletedAt == null, ct);

        var reviewCount = await context.RestaurantReviews.AsNoTracking()
            .CountAsync(r => r.UserId == userId, ct);

        var reportsAgainstContent = await context.Reports.AsNoTracking()
            .CountAsync(r =>
                (r.TargetType == ReportTargetType.Blog && context.Blogs.Any(b => b.Id == r.BlogId && b.AuthorId == userId)) ||
                (r.TargetType == ReportTargetType.Restaurant && context.Restaurants.Any(rst => rst.Id == r.RestaurantId && rst.OwnerId == userId)), ct);

        RestaurantCardDto? restaurantCard = null;
        if (user.Role.Code == "RESTAURANT_OWNER")
        {
            var restaurant = await context.Restaurants.AsNoTracking()
                .Include(r => r.CoverMedia)
                .Include(r => r.BusinessHours)
                .FirstOrDefaultAsync(r => r.OwnerId == userId && r.DeletedAt == null, ct);

            if (restaurant is not null)
            {
                restaurantCard = new RestaurantCardDto
                {
                    Id = restaurant.Id,
                    Name = restaurant.Name,
                    Slug = restaurant.Slug,
                    CoverUrl = restaurant.CoverMedia?.Url,
                    AddressLine = restaurant.AddressLine,
                    Ward = restaurant.Ward,
                    ProvinceName = restaurant.ProvinceName,
                    Latitude = restaurant.Latitude,
                    Longitude = restaurant.Longitude,
                    PriceLevel = restaurant.PriceLevel,
                    RatingAvg = restaurant.RatingAvg,
                    RatingCount = restaurant.RatingCount,
                    OperatingStatus = ToSnakeCase(restaurant.OperatingStatus),
                    IsOpenNow = IsOpenNow(restaurant.BusinessHours, clock)
                };
            }
        }

        return new ModUserDetailDto
        {
            Id = user.Id,
            Email = user.Email,
            DisplayName = user.DisplayName,
            AvatarUrl = user.AvatarMedia?.Url,
            Role = user.Role.Code,
            Status = ToSnakeCase(user.Status),
            SuspendedUntil = user.SuspendedUntil,
            LastLoginAt = user.LastLoginAt,
            CreatedAt = user.CreatedAt,
            Sanctions = sanctionDtos,
            BlogCount = blogCount,
            ReviewCount = reviewCount,
            ReportsAgainstContent = reportsAgainstContent,
            Restaurant = restaurantCard
        };
    }

    private static bool TryParseStatus(string raw, out UserStatus status)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "pending_verification":
                status = UserStatus.PendingVerification;
                return true;
            case "active":
                status = UserStatus.Active;
                return true;
            case "suspended":
                status = UserStatus.Suspended;
                return true;
            case "banned":
                status = UserStatus.Banned;
                return true;
            case "deactivated":
                status = UserStatus.Deactivated;
                return true;
            default:
                status = default;
                return false;
        }
    }

    private static string ToSnakeCase<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static bool IsOpenNow(IEnumerable<RestaurantBusinessHour> hours, TimeProvider timeProvider)
    {
        var vietnamNow = timeProvider.GetUtcNow().ToOffset(TimeSpan.FromHours(7));
        var currentDay = (short)vietnamNow.DayOfWeek;
        var previousDay = (short)((currentDay + 6) % 7);
        var currentTime = TimeOnly.FromDateTime(vietnamNow.DateTime);

        return hours.Any(hour =>
            hour.DayOfWeek == currentDay &&
            (hour.CloseTime > hour.OpenTime
                ? currentTime >= hour.OpenTime && currentTime < hour.CloseTime
                : currentTime >= hour.OpenTime)) ||
            hours.Any(hour =>
                hour.DayOfWeek == previousDay &&
                hour.CloseTime < hour.OpenTime &&
                currentTime < hour.CloseTime);
    }
}
