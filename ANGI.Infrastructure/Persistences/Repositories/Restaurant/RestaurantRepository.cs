using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
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

    /// <summary>Locks the owner's restaurant row to serialize verification state transitions.</summary>
    public Task<Domain.Entities.Restaurant?> LockOwnerRestaurantForVerificationAsync(
        int ownerId,
        CancellationToken ct)
    {
        return _context.Restaurants
            .FromSqlInterpolated($$"""
                SELECT *
                FROM core.restaurants
                WHERE owner_id = {{ownerId}}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>Adds a verification and its document children without saving immediately.</summary>
    public void AddVerification(RestaurantVerification verification)
    {
        _context.RestaurantVerifications.Add(verification);
    }

    /// <summary>Loads the owner's restaurant and its full verification history without tracking for OWN-07.</summary>
    public Task<Domain.Entities.Restaurant?> GetOwnerRestaurantWithVerificationsAsync(
        int ownerId,
        CancellationToken ct)
    {
        return _context.Restaurants
            .AsNoTracking()
            .Include(restaurant => restaurant.Verifications.OrderByDescending(item => item.SubmittedAt).ThenByDescending(item => item.Id))
                .ThenInclude(verification => verification.Documents)
                    .ThenInclude(document => document.Media)
            .Include(restaurant => restaurant.Verifications.OrderByDescending(item => item.SubmittedAt).ThenByDescending(item => item.Id))
                .ThenInclude(verification => verification.Reviewer)
                    .ThenInclude(reviewer => reviewer!.AvatarMedia)
            .AsSplitQuery()
            .FirstOrDefaultAsync(restaurant => restaurant.OwnerId == ownerId, ct);
    }

    /// <summary>Reads only the status of a verification that belongs to the given restaurant.</summary>
    public async Task<VerificationStatus?> GetVerificationStatusAsync(
        long restaurantId,
        long verificationId,
        CancellationToken ct)
    {
        return await _context.RestaurantVerifications
            .AsNoTracking()
            .Where(verification => verification.Id == verificationId && verification.RestaurantId == restaurantId)
            .Select(verification => (VerificationStatus?)verification.Status)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Reads only the id of the restaurant's newest verification (latest submission, then highest id).</summary>
    public async Task<long?> GetLatestVerificationIdAsync(long restaurantId, CancellationToken ct)
    {
        return await _context.RestaurantVerifications
            .AsNoTracking()
            .Where(verification => verification.RestaurantId == restaurantId)
            .OrderByDescending(verification => verification.SubmittedAt)
            .ThenByDescending(verification => verification.Id)
            .Select(verification => (long?)verification.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Loads and tracks only the owner's restaurant row, for changes to its own columns (OWN-05).</summary>
    public Task<Domain.Entities.Restaurant?> GetTrackedByOwnerIdAsync(int ownerId, CancellationToken ct)
    {
        return _context.Restaurants.FirstOrDefaultAsync(restaurant => restaurant.OwnerId == ownerId, ct);
    }

    /// <summary>Loads and tracks only the owner's restaurant and its business hours so OWN-04 can replace them.</summary>
    public Task<Domain.Entities.Restaurant?> GetTrackedWithBusinessHoursAsync(int ownerId, CancellationToken ct)
    {
        return _context.Restaurants
            .Include(restaurant => restaurant.BusinessHours)
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

    /// <summary>Determines whether the supplied weekly intervals contain the current Vietnam local time.</summary>
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

    /// <summary>Calculates the straight-line distance in kilometres between two geographic coordinates.</summary>
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

    /// <summary>Checks whether a restaurant exists, is verified, and is visible.</summary>
    public Task<bool> ExistsAndActiveAsync(long restaurantId, CancellationToken ct)
    {
        return _context.Restaurants
            .AsNoTracking()
            .AnyAsync(r => r.Id == restaurantId
                        && r.VerificationStatus == RestaurantVerificationStatus.Verified
                        && r.ModerationStatus == RestaurantModerationStatus.Visible, ct);
    }

    /// <summary>Loads public restaurant detail with images and business hours for REST-02.</summary>
    public Task<Domain.Entities.Restaurant?> GetPublicDetailAsync(long restaurantId, CancellationToken ct)
    {
        return _context.Restaurants
            .AsNoTracking()
            .Where(r => r.Id == restaurantId
                     && r.VerificationStatus == RestaurantVerificationStatus.Verified
                     && r.ModerationStatus == RestaurantModerationStatus.Visible)
            .Include(r => r.CoverMedia)
            .Include(r => r.Images)
                .ThenInclude(i => i.Media)
            .Include(r => r.BusinessHours)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Loads the caller's review, including its moderation state, for rating eligibility and own-review display.</summary>
    public Task<RestaurantReview?> GetTravelerReviewForRestaurantAsync(long restaurantId, int userId, CancellationToken ct)
    {
        return _context.RestaurantReviews
            .AsNoTracking()
            .Where(r => r.RestaurantId == restaurantId
                     && r.UserId == userId)
            .Include(r => r.User)
                .ThenInclude(u => u.AvatarMedia)
            .Include(r => r.Images)
                .ThenInclude(i => i.Media)
            .Include(r => r.Reply)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Loads active menu dishes for a restaurant, optionally filtered by tags and availability (REST-03).</summary>
    public async Task<IReadOnlyList<Dish>> GetActiveMenuDishesAsync(
        long restaurantId,
        IReadOnlyList<short>? tagIds,
        bool includeUnavailable,
        CancellationToken ct)
    {
        var query = _context.Dishes
            .AsNoTracking()
            .Where(d => d.RestaurantId == restaurantId && d.Status == DishStatus.Active);

        if (!includeUnavailable)
        {
            query = query.Where(d => d.IsAvailable);
        }

        if (tagIds != null && tagIds.Count > 0)
        {
            query = query.Where(d => d.DishTags.Any(dt => tagIds.Contains(dt.TagId)));
        }

        return await query
            .OrderBy(d => d.SortOrder)
            .Include(d => d.CoverMedia)
            .Include(d => d.Restaurant)
            .Include(d => d.DishTags)
                .ThenInclude(dt => dt.Tag)
            .AsSplitQuery()
            .ToListAsync(ct);
    }

    /// <summary>Loads paginated visible reviews and rating statistics for a restaurant (REST-04).</summary>
    public async Task<(IReadOnlyList<RestaurantReview> Items, long TotalItems, IReadOnlyDictionary<string, int> RatingBreakdown, decimal RatingAvg, int RatingCount)> GetReviewsAsync(
        long restaurantId,
        short? rating,
        bool? hasImage,
        string? sort,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var restaurantSummary = await _context.Restaurants
            .AsNoTracking()
            .Where(r => r.Id == restaurantId)
            .Select(r => new { r.RatingAvg, r.RatingCount })
            .FirstOrDefaultAsync(ct);

        var ratingAvg = restaurantSummary?.RatingAvg ?? 0m;
        var ratingCount = restaurantSummary?.RatingCount ?? 0;

        // Breakdown query: counts across all visible reviews for this restaurant
        var breakdownGroups = await _context.RestaurantReviews
            .AsNoTracking()
            .Where(r => r.RestaurantId == restaurantId && r.Status == ContentStatus.Visible)
            .GroupBy(r => r.Rating)
            .Select(g => new { Rating = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var ratingBreakdown = new Dictionary<string, int>
        {
            ["1"] = 0,
            ["2"] = 0,
            ["3"] = 0,
            ["4"] = 0,
            ["5"] = 0
        };

        foreach (var group in breakdownGroups)
        {
            var key = group.Rating.ToString();
            if (ratingBreakdown.ContainsKey(key))
            {
                ratingBreakdown[key] = group.Count;
            }
        }

        // Paged items query
        var query = _context.RestaurantReviews
            .AsNoTracking()
            .Where(r => r.RestaurantId == restaurantId && r.Status == ContentStatus.Visible);

        if (rating.HasValue)
        {
            query = query.Where(r => r.Rating == rating.Value);
        }

        if (hasImage == true)
        {
            query = query.Where(r => r.Images.Any());
        }
        else if (hasImage == false)
        {
            query = query.Where(r => !r.Images.Any());
        }

        var totalItems = await query.LongCountAsync(ct);

        if (string.Equals(sort, "rating_desc", StringComparison.OrdinalIgnoreCase))
        {
            query = query.OrderByDescending(r => r.Rating).ThenByDescending(r => r.CreatedAt);
        }
        else if (string.Equals(sort, "rating_asc", StringComparison.OrdinalIgnoreCase))
        {
            query = query.OrderBy(r => r.Rating).ThenByDescending(r => r.CreatedAt);
        }
        else
        {
            query = query.OrderByDescending(r => r.CreatedAt);
        }

        var skip = (page - 1) * pageSize;
        var items = await query
            .Skip(skip)
            .Take(pageSize)
            .Include(r => r.User)
                .ThenInclude(u => u.AvatarMedia)
            .Include(r => r.Images)
                .ThenInclude(i => i.Media)
            .Include(r => r.Reply)
            .AsSplitQuery()
            .ToListAsync(ct);

        return (items, totalItems, ratingBreakdown, ratingAvg, ratingCount);
    }

    /// <summary>Locks and tracks a restaurant row for review updates to safely recompute rating average and count.</summary>
    public Task<Domain.Entities.Restaurant?> LockRestaurantForReviewAsync(long restaurantId, CancellationToken ct)
    {
        return _context.Restaurants
            .FromSqlInterpolated($$"""
                SELECT *
                FROM core.restaurants
                WHERE id = {{restaurantId}} AND deleted_at IS NULL
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>Checks whether a traveler has already submitted a review for the restaurant.</summary>
    public Task<bool> HasUserReviewedRestaurantAsync(long restaurantId, int userId, CancellationToken ct)
    {
        return _context.RestaurantReviews
            .AnyAsync(r => r.RestaurantId == restaurantId && r.UserId == userId, ct);
    }

    /// <summary>Loads the user profile with avatar for constructing the review response.</summary>
    public Task<User?> GetReviewerUserAsync(int userId, CancellationToken ct)
    {
        return _context.Users
            .Include(u => u.AvatarMedia)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    /// <summary>Loads uploaded media files owned by the user.</summary>
    public async Task<IReadOnlyList<MediaFile>> GetUserReviewMediaFilesAsync(int userId, IReadOnlyList<long> mediaIds, CancellationToken ct)
    {
        if (mediaIds.Count == 0)
        {
            return Array.Empty<MediaFile>();
        }

        return await _context.MediaFiles
            .Where(m => mediaIds.Contains(m.Id) && m.UploaderId == userId)
            .ToListAsync(ct);
    }

    /// <summary>Checks whether a roadmap item belongs to the user and references a dish of this restaurant.</summary>
    public Task<bool> IsRoadmapItemValidForRestaurantAsync(int userId, long roadmapItemId, long restaurantId, CancellationToken ct)
    {
        return _context.RoadmapItems
            .AnyAsync(ri => ri.Id == roadmapItemId 
                         && ri.RoadmapDay.Roadmap.UserId == userId 
                         && ri.Dish.RestaurantId == restaurantId, ct);
    }

    /// <summary>Adds a restaurant review aggregate to the current unit of work.</summary>
    public void AddReview(RestaurantReview review)
    {
        _context.RestaurantReviews.Add(review);
    }

    /// <summary>Adds a notification to the current unit of work.</summary>
    public void AddNotification(Domain.Entities.Notification notification)
    {
        _context.Notifications.Add(notification);
    }
}
