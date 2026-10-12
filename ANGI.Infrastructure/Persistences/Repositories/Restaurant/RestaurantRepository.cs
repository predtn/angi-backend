using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;
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

    /// <summary>Retrieves a paginated list of verified, visible restaurants matching the query criteria.</summary>
    public async Task<Paged<RestaurantCardDto>> BrowseAsync(
        BrowseRestaurantsRequestDto request,
        DateTimeOffset currentUtcTime,
        CancellationToken ct)
    {
        var query = _context.Restaurants
            .AsNoTracking()
            .Where(r => r.VerificationStatus == RestaurantVerificationStatus.Verified
                     && r.ModerationStatus == RestaurantModerationStatus.Visible);

        if (!string.IsNullOrWhiteSpace(request.Q))
        {
            var term = $"%{request.Q.Trim()}%";
            query = query.Where(r => EF.Functions.ILike(r.Name, term) || EF.Functions.ILike(r.AddressLine, term));
        }

        if (!string.IsNullOrWhiteSpace(request.Province))
        {
            var provinceTerm = $"%{request.Province.Trim()}%";
            query = query.Where(r => r.ProvinceName != null && EF.Functions.ILike(r.ProvinceName, provinceTerm));
        }

        if (!string.IsNullOrWhiteSpace(request.District))
        {
            var districtTerm = $"%{request.District.Trim()}%";
            query = query.Where(r => r.District != null && EF.Functions.ILike(r.District, districtTerm));
        }

        if (request.PriceLevel.HasValue)
        {
            query = query.Where(r => r.PriceLevel == request.PriceLevel.Value);
        }

        if (request.MinRating.HasValue)
        {
            query = query.Where(r => r.RatingAvg >= request.MinRating.Value);
        }

        decimal? cosLatVal = null;
        if (request.Lat.HasValue && request.Lng.HasValue)
        {
            var rad = (double)(request.Lat.Value * (decimal)Math.PI / 180m);
            cosLatVal = (decimal)Math.Cos(rad);
        }

        if (request.RadiusKm.HasValue && request.Lat.HasValue && request.Lng.HasValue)
        {
            var lat = request.Lat.Value;
            var lng = request.Lng.Value;
            var radius = request.RadiusKm.Value;
            var deltaLat = radius / 111.0m;
            var cosLat = cosLatVal ?? 1.0m;
            var deltaLng = cosLat > 0 ? radius / (111.0m * cosLat) : radius / 111.0m;
            var lngWeight = 111.0m * (cosLat > 0 ? cosLat : 1.0m);
            var radiusSquared = radius * radius;

            query = query.Where(r => r.Latitude >= lat - deltaLat
                                  && r.Latitude <= lat + deltaLat
                                  && r.Longitude >= lng - deltaLng
                                  && r.Longitude <= lng + deltaLng
                                  && ((r.Latitude - lat) * 111.0m) * ((r.Latitude - lat) * 111.0m) +
                                     ((r.Longitude - lng) * lngWeight) * ((r.Longitude - lng) * lngWeight) <= radiusSquared);
        }

        var vietnamNow = currentUtcTime.ToOffset(TimeSpan.FromHours(7));
        var currentDay = (short)vietnamNow.DayOfWeek;
        var previousDay = (short)((currentDay + 6) % 7);
        var currentTime = TimeOnly.FromDateTime(vietnamNow.DateTime);

        if (request.OpenNow == true)
        {
            query = query.Where(r => r.OperatingStatus == RestaurantOperatingStatus.Open &&
                r.BusinessHours.Any(h =>
                    (h.DayOfWeek == currentDay && (h.CloseTime > h.OpenTime
                        ? currentTime >= h.OpenTime && currentTime < h.CloseTime
                        : currentTime >= h.OpenTime))
                    ||
                    (h.DayOfWeek == previousDay && h.CloseTime < h.OpenTime && currentTime < h.CloseTime)));
        }

        var totalItems = await query.LongCountAsync(ct);
        if (totalItems == 0)
        {
            return new Paged<RestaurantCardDto>
            {
                Items = Array.Empty<RestaurantCardDto>(),
                Page = request.Page,
                PageSize = request.PageSize,
                TotalItems = 0,
                TotalPages = 0
            };
        }

        if (string.Equals(request.Sort, "distance", StringComparison.OrdinalIgnoreCase)
            && request.Lat.HasValue
            && request.Lng.HasValue)
        {
            var userLat = request.Lat.Value;
            var userLng = request.Lng.Value;
            var cosLat = cosLatVal ?? 1.0m;
            var lngWeight = 111.0m * (cosLat > 0 ? cosLat : 1.0m);

            query = query.OrderBy(r =>
                ((r.Latitude - userLat) * 111.0m) * ((r.Latitude - userLat) * 111.0m) +
                ((r.Longitude - userLng) * lngWeight) * ((r.Longitude - userLng) * lngWeight));
        }
        else if (string.Equals(request.Sort, "newest", StringComparison.OrdinalIgnoreCase))
        {
            query = query.OrderByDescending(r => r.CreatedAt);
        }
        else
        {
            query = query.OrderByDescending(r => r.RatingAvg).ThenByDescending(r => r.RatingCount);
        }

        var skip = (request.Page - 1) * request.PageSize;
        var totalPages = (int)Math.Ceiling((double)totalItems / request.PageSize);

        var pagedEntities = await query
            .Skip(skip)
            .Take(request.PageSize)
            .Include(r => r.CoverMedia)
            .Include(r => r.BusinessHours)
            .ToListAsync(ct);

        var items = pagedEntities.Select(r => new RestaurantCardDto
        {
            Id = r.Id,
            Name = r.Name,
            Slug = r.Slug,
            CoverUrl = r.CoverMedia?.Url,
            AddressLine = r.AddressLine,
            District = r.District,
            ProvinceName = r.ProvinceName,
            Latitude = r.Latitude,
            Longitude = r.Longitude,
            PriceLevel = r.PriceLevel,
            RatingAvg = r.RatingAvg,
            RatingCount = r.RatingCount,
            OperatingStatus = ToApiValue(r.OperatingStatus),
            IsOpenNow = CalculateIsOpenNow(r, currentDay, previousDay, currentTime),
            DistanceKm = request.Lat.HasValue && request.Lng.HasValue
                ? CalculateHaversineDistance(request.Lat.Value, request.Lng.Value, r.Latitude, r.Longitude)
                : null
        }).ToArray();

        return new Paged<RestaurantCardDto>
        {
            Items = items,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalItems = totalItems,
            TotalPages = totalPages
        };
    }

    private static string ToApiValue<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static bool CalculateIsOpenNow(
        Domain.Entities.Restaurant restaurant,
        short currentDay,
        short previousDay,
        TimeOnly currentTime)
    {
        if (restaurant.OperatingStatus != RestaurantOperatingStatus.Open)
        {
            return false;
        }

        return restaurant.BusinessHours.Any(h =>
            (h.DayOfWeek == currentDay && (h.CloseTime > h.OpenTime
                ? currentTime >= h.OpenTime && currentTime < h.CloseTime
                : currentTime >= h.OpenTime))
            ||
            (h.DayOfWeek == previousDay && h.CloseTime < h.OpenTime && currentTime < h.CloseTime));
    }

    private static decimal CalculateHaversineDistance(decimal lat1, decimal lng1, decimal lat2, decimal lng2)
    {
        const double EarthRadiusKm = 6371.0;
        var dLat = (double)(lat2 - lat1) * Math.PI / 180.0;
        var dLng = (double)(lng2 - lng1) * Math.PI / 180.0;
        var radLat1 = (double)lat1 * Math.PI / 180.0;
        var radLat2 = (double)lat2 * Math.PI / 180.0;

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(radLat1) * Math.Cos(radLat2) *
                Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return Math.Round((decimal)(EarthRadiusKm * c), 1);
    }
}
