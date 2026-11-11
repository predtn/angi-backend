using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Moderation;

/// <summary>Defines persistence operations used by the restaurant verification moderation workflow.</summary>
public interface IVerificationReviewRepository
{
    /// <summary>Checks the user's effective database permission, honoring role defaults and user overrides.</summary>
    Task<bool> HasEffectivePermissionAsync(int userId, string permissionCode, CancellationToken ct);

    /// <summary>Returns a read-only page of verification aggregates ordered from oldest to newest.</summary>
    Task<(IReadOnlyList<RestaurantVerification> Items, long TotalItems)> ListAsync(
        ListVerificationsRequestDto request,
        CancellationToken ct);

    /// <summary>Loads the complete read-only aggregate required by MOD-V2.</summary>
    Task<RestaurantVerification?> GetDetailAsync(long verificationId, CancellationToken ct);

    /// <summary>Locks and tracks a verification and its restaurant for one atomic MOD-V3 decision.</summary>
    Task<RestaurantVerification?> LockForDecisionAsync(long verificationId, CancellationToken ct);

    /// <summary>Loads the saved reviewer navigation so the MOD-V3 response contains UserSummary.</summary>
    Task LoadReviewerAsync(RestaurantVerification verification, CancellationToken ct);

    /// <summary>Adds an owner notification to the current unit of work.</summary>
    void AddNotification(Domain.Entities.Notification notification);
}
