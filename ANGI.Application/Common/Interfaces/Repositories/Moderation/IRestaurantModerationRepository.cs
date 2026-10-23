using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Moderation;

/// <summary>Provides moderation reads and tracked writes without owning transactions or business rules.</summary>
public interface IRestaurantModerationRepository
{
    /// <summary>Lists restaurants including unverified and hidden rows, excluding soft-deleted rows.</summary>
    Task<Paged<ModRestaurantListItem>> ListAsync(ListModRestaurantsRequestDto request, CancellationToken ct);
    /// <summary>Loads the complete MOD-R2 restaurant aggregate without tracking.</summary>
    Task<Domain.Entities.Restaurant?> GetDetailAsync(long id, CancellationToken ct);
    /// <summary>Loads the twenty most recent reports for the restaurant without tracking.</summary>
    Task<IReadOnlyList<Report>> GetRecentReportsAsync(long restaurantId, CancellationToken ct);
    /// <summary>Loads the twenty most recent restaurant audit entries without tracking.</summary>
    Task<IReadOnlyList<AuditLog>> GetRecentAuditAsync(long restaurantId, CancellationToken ct);
    /// <summary>Locks and tracks a restaurant for a moderation transaction.</summary>
    Task<Domain.Entities.Restaurant?> LockRestaurantAsync(long id, CancellationToken ct);
    /// <summary>Reads the restaurant identifier of an existing non-deleted dish parent.</summary>
    Task<long?> GetDishRestaurantIdAsync(int dishId, CancellationToken ct);
    /// <summary>Locks and tracks a dish after its parent restaurant has been locked.</summary>
    Task<Dish?> LockDishAsync(int id, CancellationToken ct);
    /// <summary>Reads only the immutable type and identifier of the report target.</summary>
    Task<ReportTarget?> GetReportTargetAsync(long reportId, CancellationToken ct);
    /// <summary>Locks the selected report and open reports on its target in identifier order.</summary>
    Task<IReadOnlyList<Report>> LockReportsAsync(ReportTarget target, long reportId, CancellationToken ct);
    /// <summary>Locks and tracks an existing blog target.</summary>
    Task<Blog?> LockBlogAsync(long id, CancellationToken ct);
    /// <summary>Loads names, public user data, evidence metadata and the open report count.</summary>
    Task<ReportDisplayData> GetReportDisplayDataAsync(ReportTarget target, IReadOnlyList<Report> reports, CancellationToken ct);
    /// <summary>Locks the account being sanctioned, including its current database role.</summary>
    Task<User?> LockUserAsync(int userId, CancellationToken ct);
    /// <summary>Loads unrevoked sessions for a sanctioned user.</summary>
    Task<IReadOnlyList<UserSession>> GetSessionsAsync(int userId, CancellationToken ct);
    /// <summary>Locks a bounded batch of non-deleted suspended accounts whose deadline has passed.</summary>
    Task<IReadOnlyList<User>> LockExpiredSuspensionsAsync(DateTime now, CancellationToken ct);
    /// <summary>Adds a sanction to the current unit of work.</summary>
    void AddSanction(UserSanction sanction);
    /// <summary>Adds a notification to the current unit of work.</summary>
    void AddNotification(Domain.Entities.Notification notification);
}
