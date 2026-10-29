using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.Common.Models.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Moderation;

/// <summary>Implements PostgreSQL reads and ordered row locks for restaurant and report moderation.</summary>
public sealed class RestaurantModerationRepository(ANGIContext context) : IRestaurantModerationRepository
{
    /// <summary>Applies all MOD-RP1 filters with stable paging and a wide offset calculation.</summary>
    public async Task<Paged<Report>> ListReportsAsync(ListReportsRequestDto request, CancellationToken ct)
    {
        var type = Parse<ReportTargetType>(request.TargetType);
        var query = context.Reports.AsNoTracking().Where(report => report.TargetType == type);
        if (request.Status is not null)
        {
            var status = Parse<ReportStatus>(request.Status);
            query = query.Where(report => report.Status == status);
        }
        if (request.ReasonCode is not null)
        {
            var reason = Parse<ReportReasonCode>(request.ReasonCode);
            query = query.Where(report => report.ReasonCode == reason);
        }
        if (request.TargetId.HasValue)
            query = query.Where(report => type == ReportTargetType.Restaurant ? report.RestaurantId == request.TargetId : report.BlogId == request.TargetId);
        var count = await query.LongCountAsync(ct);
        var offset = ((long)request.Page - 1) * request.PageSize;
        var items = offset > int.MaxValue ? [] : await query.OrderByDescending(report => report.CreatedAt).ThenByDescending(report => report.Id)
            .Skip((int)offset).Take(request.PageSize).ToListAsync(ct);
        return new() { Items = items, Page = request.Page, PageSize = request.PageSize, TotalItems = count,
            TotalPages = (int)Math.Ceiling((double)count / request.PageSize) };
    }

    /// <summary>Loads report content without tracking after the use case has checked its target permission.</summary>
    public Task<Report?> GetReportAsync(long reportId, CancellationToken ct) =>
        context.Reports.AsNoTracking().SingleOrDefaultAsync(report => report.Id == reportId, ct);

    /// <summary>Returns only other open reports sharing both target type and target identifier.</summary>
    public async Task<IReadOnlyList<Report>> GetRelatedReportsAsync(ReportTarget target, long reportId, CancellationToken ct) =>
        await context.Reports.AsNoTracking().Where(report => report.Id != reportId && report.TargetType == target.Type &&
            (target.Type == ReportTargetType.Restaurant ? report.RestaurantId == target.Id : report.BlogId == target.Id) &&
            (report.Status == ReportStatus.Pending || report.Status == ReportStatus.InReview))
            .OrderByDescending(report => report.CreatedAt).ThenByDescending(report => report.Id).ToListAsync(ct);

    /// <summary>Acquires the same report row lock used by resolution so stale claims cannot overwrite conclusions.</summary>
    public Task<Report?> LockReportAsync(long reportId, CancellationToken ct) => context.Reports
        .FromSqlInterpolated($"SELECT * FROM core.reports WHERE id = {reportId} FOR UPDATE").SingleOrDefaultAsync(ct);

    /// <summary>Projects the reported restaurant's card fields even when hidden or unverified, respecting soft deletion.</summary>
    public Task<Domain.Entities.Restaurant?> GetReportRestaurantAsync(long restaurantId, CancellationToken ct) =>
        context.Restaurants.AsNoTracking().Where(restaurant => restaurant.Id == restaurantId)
            .Select(restaurant => new Domain.Entities.Restaurant
            {
                Id = restaurant.Id, Name = restaurant.Name, Slug = restaurant.Slug, AddressLine = restaurant.AddressLine,
                Ward = restaurant.Ward, ProvinceName = restaurant.ProvinceName, Latitude = restaurant.Latitude,
                Longitude = restaurant.Longitude, PriceLevel = restaurant.PriceLevel, RatingAvg = restaurant.RatingAvg,
                RatingCount = restaurant.RatingCount, OperatingStatus = restaurant.OperatingStatus,
                CoverMedia = restaurant.CoverMediaId == null ? null : new MediaFile { Url = restaurant.CoverMedia!.Url },
                BusinessHours = restaurant.BusinessHours.Select(hour => new RestaurantBusinessHour
                { DayOfWeek = hour.DayOfWeek, OpenTime = hour.OpenTime, CloseTime = hour.CloseTime }).ToList()
            }).SingleOrDefaultAsync(ct);

    /// <summary>Loads the reported blog including hidden or removed states, ordered media and the caller's like.</summary>
    public Task<Blog?> GetReportBlogAsync(long blogId, int callerId, CancellationToken ct) => context.Blogs.AsNoTracking()
        .Include(blog => blog.Author).ThenInclude(author => author.AvatarMedia)
        .Include(blog => blog.CoverMedia).Include(blog => blog.Images).ThenInclude(image => image.Media)
        .Include(blog => blog.Likes.Where(like => like.UserId == callerId)).AsSplitQuery().SingleOrDefaultAsync(blog => blog.Id == blogId, ct);

    /// <summary>Loads a page's display data in a fixed number of queries rather than querying each report separately.</summary>
    public async Task<IReadOnlyDictionary<ReportTarget, ReportDisplayData>> GetReportDisplaysAsync(IReadOnlyList<Report> reports, CancellationToken ct)
    {
        if (reports.Count == 0) return new Dictionary<ReportTarget, ReportDisplayData>();
        var restaurantIds = reports.Where(report => report.TargetType == ReportTargetType.Restaurant).Select(report => report.RestaurantId!.Value).Distinct().ToArray();
        var blogIds = reports.Where(report => report.TargetType == ReportTargetType.Blog).Select(report => report.BlogId!.Value).Distinct().ToArray();
        var users = reports.Select(report => report.ReporterId).Concat(reports.Where(report => report.HandledBy.HasValue).Select(report => report.HandledBy!.Value)).Distinct().ToArray();
        var evidence = reports.SelectMany(report => JsonSerializer.Deserialize<long[]>(report.Evidence) ?? []).Distinct().ToArray();
        var userMap = await context.Users.IgnoreQueryFilters().AsNoTracking().Where(user => users.Contains(user.Id))
            .Select(user => new User { Id = user.Id, DisplayName = user.DisplayName,
                AvatarMedia = user.AvatarMediaId == null ? null : new MediaFile { Url = user.AvatarMedia!.Url } }).ToDictionaryAsync(user => user.Id, ct);
        var mediaMap = await context.MediaFiles.AsNoTracking().Where(media => evidence.Contains(media.Id))
            .Select(media => new MediaFile { Id = media.Id, StorageKey = media.StorageKey }).ToDictionaryAsync(media => media.Id, ct);
        var restaurants = await context.Restaurants.AsNoTracking().Where(restaurant => restaurantIds.Contains(restaurant.Id))
            .Select(restaurant => new { restaurant.Id, restaurant.Name }).ToDictionaryAsync(restaurant => restaurant.Id, restaurant => restaurant.Name, ct);
        var blogs = await context.Blogs.AsNoTracking().Where(blog => blogIds.Contains(blog.Id))
            .Select(blog => new { blog.Id, blog.Title }).ToDictionaryAsync(blog => blog.Id, blog => blog.Title, ct);
        var counts = await context.Reports.AsNoTracking().Where(report =>
            (report.TargetType == ReportTargetType.Restaurant && restaurantIds.Contains(report.RestaurantId!.Value) ||
             report.TargetType == ReportTargetType.Blog && blogIds.Contains(report.BlogId!.Value)) &&
            (report.Status == ReportStatus.Pending || report.Status == ReportStatus.InReview))
            .GroupBy(report => new { report.TargetType, report.RestaurantId, report.BlogId })
            .Select(group => new { group.Key.TargetType, Id = (group.Key.RestaurantId ?? group.Key.BlogId)!.Value, Count = group.Count() }).ToListAsync(ct);
        var countMap = counts.ToDictionary(item => new ReportTarget(item.TargetType, item.Id), item => item.Count);
        return reports.Select(report => new ReportTarget(report.TargetType, (report.RestaurantId ?? report.BlogId)!.Value)).Distinct()
            .ToDictionary(target => target, target => new ReportDisplayData(
                (target.Type == ReportTargetType.Restaurant ? restaurants : blogs).GetValueOrDefault(target.Id, string.Empty),
                userMap, mediaMap, countMap.GetValueOrDefault(target)));
    }

    /// <summary>Projects a bounded page and applies each MOD-R1 filter, including pending and in-review counts.</summary>
    public async Task<Paged<ModRestaurantListItem>> ListAsync(ListModRestaurantsRequestDto request, CancellationToken ct)
    {
        var query = context.Restaurants.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Q))
        {
            var term = request.Q.Trim().ToLowerInvariant();
            query = query.Where(r => r.Name.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(request.Province)) query = query.Where(r => r.ProvinceName == request.Province.Trim());
        if (request.VerificationStatus is not null)
        {
            var status = Parse<RestaurantVerificationStatus>(request.VerificationStatus);
            query = query.Where(r => r.VerificationStatus == status);
        }
        if (request.OperatingStatus is not null)
        {
            var status = Parse<RestaurantOperatingStatus>(request.OperatingStatus);
            query = query.Where(r => r.OperatingStatus == status);
        }
        if (request.ModerationStatus is not null)
        {
            var status = Parse<RestaurantModerationStatus>(request.ModerationStatus);
            query = query.Where(r => r.ModerationStatus == status);
        }
        if (request.HasPendingReports.HasValue)
        {
            var hasReports = request.HasPendingReports.Value;
            query = query.Where(r => context.Reports.Any(report => report.TargetType == ReportTargetType.Restaurant &&
                report.RestaurantId == r.Id && (report.Status == ReportStatus.Pending || report.Status == ReportStatus.InReview)) == hasReports);
        }
        var count = await query.LongCountAsync(ct);
        var offset = ((long)request.Page - 1) * request.PageSize;
        var items = offset > int.MaxValue ? [] : await query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Skip((int)offset).Take(request.PageSize)
            .Select(r => new ModRestaurantListItem(new Domain.Entities.Restaurant
            {
                Id = r.Id, Name = r.Name, Slug = r.Slug, AddressLine = r.AddressLine,
                Ward = r.Ward, ProvinceName = r.ProvinceName, Latitude = r.Latitude, Longitude = r.Longitude,
                PriceLevel = r.PriceLevel, RatingAvg = r.RatingAvg, RatingCount = r.RatingCount,
                CoverMedia = r.CoverMediaId == null ? null : new MediaFile { Url = r.CoverMedia!.Url },
                OperatingStatus = r.OperatingStatus, VerificationStatus = r.VerificationStatus, ModerationStatus = r.ModerationStatus,
                BusinessHours = r.BusinessHours.Select(h => new RestaurantBusinessHour
                {
                    DayOfWeek = h.DayOfWeek, OpenTime = h.OpenTime, CloseTime = h.CloseTime
                }).ToList()
            }, context.Reports.Count(report => report.TargetType == ReportTargetType.Restaurant && report.RestaurantId == r.Id &&
                (report.Status == ReportStatus.Pending || report.Status == ReportStatus.InReview))))
            .ToListAsync(ct);
        return new() { Items = items, Page = request.Page, PageSize = request.PageSize, TotalItems = count,
            TotalPages = (int)Math.Ceiling((double)count / request.PageSize) };
    }

    /// <summary>Loads the full read-only graph required to map OwnerRestaurant and its running menu.</summary>
    public Task<Domain.Entities.Restaurant?> GetDetailAsync(long id, CancellationToken ct) => context.Restaurants.AsNoTracking()
        .Include(r => r.Owner).ThenInclude(u => u.Role)
        .Include(r => r.Owner).ThenInclude(u => u.AvatarMedia)
        .Include(r => r.CoverMedia)
        .Include(r => r.Images).ThenInclude(i => i.Media)
        .Include(r => r.BusinessHours)
        .Include(r => r.Verifications.OrderByDescending(v => v.SubmittedAt).ThenByDescending(v => v.Id).Take(1))
            .ThenInclude(v => v.Documents).ThenInclude(d => d.Media)
        .Include(r => r.Verifications.OrderByDescending(v => v.SubmittedAt).ThenByDescending(v => v.Id).Take(1))
            .ThenInclude(v => v.Reviewer).ThenInclude(u => u!.AvatarMedia)
        .Include(r => r.MenuSubmissions.Where(s => s.Status == MenuSubmissionStatus.Draft || s.Status == MenuSubmissionStatus.Pending))
        .Include(r => r.Dishes.Where(d => d.Status != DishStatus.Removed)).ThenInclude(d => d.CoverMedia)
        .Include(r => r.Dishes.Where(d => d.Status != DishStatus.Removed)).ThenInclude(d => d.DishTags).ThenInclude(t => t.Tag)
        .AsSplitQuery().SingleOrDefaultAsync(r => r.Id == id, ct);

    /// <summary>Loads the newest twenty reports about this restaurant using a deterministic tie-breaker.</summary>
    public async Task<IReadOnlyList<Report>> GetRecentReportsAsync(long restaurantId, CancellationToken ct) =>
        await context.Reports.AsNoTracking().Where(r => r.TargetType == ReportTargetType.Restaurant && r.RestaurantId == restaurantId)
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Take(20).ToListAsync(ct);

    /// <summary>Loads the newest twenty restaurant audit list records with public actor and subject data.</summary>
    public async Task<IReadOnlyList<AuditLog>> GetRecentAuditAsync(long restaurantId, CancellationToken ct) =>
        await context.AuditLogs.AsNoTracking().Where(a => a.EntityType == AuditEntityTypes.Restaurant && a.EntityId == restaurantId)
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(20)
            .Select(a => new AuditLog
            {
                Id = a.Id, ActorId = a.ActorId, ActorRole = a.ActorRole, Action = a.Action, EntityType = a.EntityType,
                EntityId = a.EntityId, CreatedAt = a.CreatedAt,
                Actor = a.Actor == null ? null : new User { Id = a.Actor.Id, DisplayName = a.Actor.DisplayName },
                SubjectUser = a.SubjectUser == null ? null : new User { Id = a.SubjectUser.Id, DisplayName = a.SubjectUser.DisplayName,
                    AvatarMedia = a.SubjectUser.AvatarMediaId == null ? null : new MediaFile { Url = a.SubjectUser.AvatarMedia!.Url } }
            }).ToListAsync(ct);

    /// <summary>Locks the parent restaurant before any dependent report or dish locks are acquired.</summary>
    public Task<Domain.Entities.Restaurant?> LockRestaurantAsync(long id, CancellationToken ct) => context.Restaurants
        .FromSqlInterpolated($"SELECT * FROM core.restaurants WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);

    /// <summary>Reads only the parent identifier and respects the parent's soft-delete filter.</summary>
    public Task<long?> GetDishRestaurantIdAsync(int dishId, CancellationToken ct) => context.Dishes.AsNoTracking()
        .Where(d => d.Id == dishId && d.Restaurant.DeletedAt == null).Select(d => (long?)d.RestaurantId).SingleOrDefaultAsync(ct);

    /// <summary>Locks a dish after its parent so menu and moderation writes cannot race.</summary>
    public Task<Dish?> LockDishAsync(int id, CancellationToken ct) => context.Dishes
        .FromSqlInterpolated($"SELECT * FROM core.dishes WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);

    /// <summary>Reads the immutable report target without loading report content.</summary>
    public Task<ReportTarget?> GetReportTargetAsync(long reportId, CancellationToken ct) => context.Reports.AsNoTracking()
        .Where(r => r.Id == reportId).Select(r => new ReportTarget(r.TargetType, (r.RestaurantId ?? r.BlogId)!.Value)).SingleOrDefaultAsync(ct);

    /// <summary>Locks the selected and open reports in identifier order without loading the closed report history.</summary>
    public async Task<IReadOnlyList<Report>> LockReportsAsync(ReportTarget target, long reportId, CancellationToken ct) => target.Type == ReportTargetType.Restaurant
        ? await context.Reports.FromSqlInterpolated($"SELECT * FROM core.reports WHERE target_type = 'restaurant' AND restaurant_id = {target.Id} AND (id = {reportId} OR status IN ('pending', 'in_review')) ORDER BY id FOR UPDATE").ToListAsync(ct)
        : await context.Reports.FromSqlInterpolated($"SELECT * FROM core.reports WHERE target_type = 'blog' AND blog_id = {target.Id} AND (id = {reportId} OR status IN ('pending', 'in_review')) ORDER BY id FOR UPDATE").ToListAsync(ct);

    /// <summary>Locks a non-deleted blog before resolving any reports about it.</summary>
    public Task<Blog?> LockBlogAsync(long id, CancellationToken ct) => context.Blogs
        .FromSqlInterpolated($"SELECT * FROM core.blogs WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);

    /// <summary>Batch-loads public report identities and evidence metadata without tracking or per-report queries.</summary>
    public async Task<ReportDisplayData> GetReportDisplayDataAsync(ReportTarget target, IReadOnlyList<Report> reports, CancellationToken ct)
    {
        var users = reports.Select(r => r.ReporterId).Concat(reports.Where(r => r.HandledBy.HasValue).Select(r => r.HandledBy!.Value)).Distinct().ToArray();
        var evidence = reports.SelectMany(r => JsonSerializer.Deserialize<long[]>(r.Evidence) ?? []).Distinct().ToArray();
        var userMap = await context.Users.IgnoreQueryFilters().AsNoTracking().Where(u => users.Contains(u.Id))
            .Select(u => new User { Id = u.Id, DisplayName = u.DisplayName,
                AvatarMedia = u.AvatarMediaId == null ? null : new MediaFile { Url = u.AvatarMedia!.Url } }).ToDictionaryAsync(u => u.Id, ct);
        var mediaMap = await context.MediaFiles.AsNoTracking().Where(m => evidence.Contains(m.Id))
            .Select(m => new MediaFile { Id = m.Id, StorageKey = m.StorageKey }).ToDictionaryAsync(m => m.Id, ct);
        var name = target.Type == ReportTargetType.Restaurant
            ? await context.Restaurants.AsNoTracking().Where(r => r.Id == target.Id).Select(r => r.Name).SingleOrDefaultAsync(ct)
            : await context.Blogs.AsNoTracking().Where(b => b.Id == target.Id).Select(b => b.Title).SingleOrDefaultAsync(ct);
        var openCount = await context.Reports.AsNoTracking().CountAsync(r => r.TargetType == target.Type &&
            (target.Type == ReportTargetType.Restaurant ? r.RestaurantId == target.Id : r.BlogId == target.Id) &&
            (r.Status == ReportStatus.Pending || r.Status == ReportStatus.InReview), ct);
        return new(name ?? string.Empty, userMap, mediaMap, openCount);
    }

    /// <summary>Locks an account and loads its current role before applying a sanction.</summary>
    public Task<User?> LockUserAsync(int userId, CancellationToken ct) => context.Users
        .FromSqlInterpolated($"SELECT * FROM core.users WHERE id = {userId} FOR UPDATE").Include(u => u.Role).SingleOrDefaultAsync(ct);

    /// <summary>Tracks unrevoked sessions for atomic revocation with a suspension or ban.</summary>
    public async Task<IReadOnlyList<UserSession>> GetSessionsAsync(int userId, CancellationToken ct) =>
        await context.UserSessions.Where(s => s.UserId == userId && s.RevokedAt == null).ToListAsync(ct);

    /// <summary>Claims up to one hundred due accounts without waiting for another sanction or expiry worker.</summary>
    public async Task<IReadOnlyList<User>> LockExpiredSuspensionsAsync(DateTime now, CancellationToken ct) =>
        await context.Users.FromSqlInterpolated($"SELECT * FROM core.users WHERE status = 'suspended' AND suspended_until <= {now} AND deleted_at IS NULL ORDER BY id LIMIT 100 FOR UPDATE SKIP LOCKED")
            .ToListAsync(ct);

    /// <summary>Stages a sanction without saving or starting a transaction.</summary>
    public void AddSanction(UserSanction sanction) => context.UserSanctions.Add(sanction);

    /// <summary>Stages a notification in the caller's transaction.</summary>
    public void AddNotification(Domain.Entities.Notification notification) => context.Notifications.Add(notification);

    /// <summary>Parses a filter already validated by the application layer.</summary>
    private static T Parse<T>(string value) where T : struct, Enum => Enum.GetValues<T>()
        .Single(item => JsonNamingPolicy.SnakeCaseLower.ConvertName(item.ToString()) == value);
}
