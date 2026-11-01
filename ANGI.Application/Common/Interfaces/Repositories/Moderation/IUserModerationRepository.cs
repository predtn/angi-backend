using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Moderation;

/// <summary>Repository for querying user accounts and details for moderation purposes.</summary>
public interface IUserModerationRepository
{
    /// <summary>Lists non-staff users matching the given filters and pagination.</summary>
    Task<Paged<AdminUserCardDto>> ListAsync(ListUsersRequestDto request, CancellationToken ct);

    /// <summary>Gets complete account moderation details including sanctions, stats and restaurant.</summary>
    Task<ModUserDetailDto?> GetDetailAsync(int userId, CancellationToken ct);

    /// <summary>Locks a non-deleted user account with role and avatar for moderation update.</summary>
    Task<User?> LockUserAsync(int userId, CancellationToken ct);

    /// <summary>Queries active unlifted sanctions for a user.</summary>
    Task<IReadOnlyList<UserSanction>> GetActiveSanctionsAsync(int userId, DateTime now, CancellationToken ct);

    /// <summary>Adds a notification to the current unit of work.</summary>
    void AddNotification(Domain.Entities.Notification notification);
}
