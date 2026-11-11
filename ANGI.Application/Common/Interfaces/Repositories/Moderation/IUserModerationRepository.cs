using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Moderation;

/// <summary>Repository for querying and updating user accounts and sanctions for moderation purposes.</summary>
public interface IUserModerationRepository : IUserSanctionRepository
{
    /// <summary>Lists non-staff users matching the given filters and pagination.</summary>
    Task<Paged<AdminUserCardDto>> ListAsync(ListUsersRequestDto request, CancellationToken ct);

    /// <summary>Gets complete account moderation details including sanctions, stats and restaurant.</summary>
    Task<ModUserDetailDto?> GetDetailAsync(int userId, CancellationToken ct);

    /// <summary>Loads the public profile summary of a moderator/user by id.</summary>
    Task<UserSummaryDto?> GetUserSummaryAsync(int userId, CancellationToken ct);

    /// <summary>Queries active unlifted sanctions for a user.</summary>
    Task<IReadOnlyList<UserSanction>> GetActiveSanctionsAsync(int userId, DateTime now, CancellationToken ct);

    /// <summary>Checks whether a non-deleted user account exists.</summary>
    Task<bool> UserExistsAsync(int userId, CancellationToken ct);

    /// <summary>Lists paginated audit logs where the user is the actor or the subject user.</summary>
    Task<Paged<AuditLogDto>> GetUserAuditLogsAsync(int userId, GetUserAuditLogsRequestDto request, CancellationToken ct);
}
