using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Administration;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.Common.Interfaces.Repositories.Administration;

/// <summary>Provides persistence operations for MOD account administration.</summary>
public interface IAdministrationModRepository
{
    /// <summary>Returns a deterministic page of MOD account projections matching optional filters.</summary>
    Task<Paged<AdminUserSnapshot>> GetModsAsync(
        string? query,
        UserStatus? status,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>Loads one MOD account as read-only response data.</summary>
    Task<AdminUserSnapshot?> GetModAsync(int userId, CancellationToken ct);

    /// <summary>Loads and tracks one MOD account for a serialized update.</summary>
    Task<User?> GetModForUpdateAsync(int userId, CancellationToken ct);

    /// <summary>Counts all restaurant verifications, menu submissions, and reports handled by a MOD.</summary>
    Task<ModHandledSnapshot> GetHandledAsync(int userId, CancellationToken ct);

    /// <summary>Checks the non-deleted unique-email domain.</summary>
    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken ct);

    /// <summary>Tracks a new MOD without persisting it until the Unit of Work saves.</summary>
    void AddMod(User user);

    /// <summary>Tracks the invitation password-reset token owned by a new MOD.</summary>
    void AddPasswordResetToken(UserToken token);

    /// <summary>Tracks permission overrides whose principal is a newly added MOD.</summary>
    void AddPermissionOverrides(IEnumerable<UserPermission> overrides);

    /// <summary>Loads active sessions so deactivation can revoke every session atomically.</summary>
    Task<IReadOnlyList<UserSession>> GetActiveSessionsAsync(int userId, CancellationToken ct);
}
