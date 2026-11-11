using ANGI.Application.Common.Models.Administration;

namespace ANGI.Application.Common.Interfaces.Repositories.Administration;

/// <summary>Provides persistence operations for permission administration.</summary>
public interface IAdministrationPermissionRepository
{
    /// <summary>Loads the permission catalog and current MOD role defaults as read-only data.</summary>
    Task<PermissionCatalogSnapshot> GetCatalogAsync(CancellationToken ct);

    /// <summary>Loads one active MOD account's defaults and overrides, or null when no such MOD exists.</summary>
    Task<ModPermissionsSnapshot?> GetModPermissionsAsync(int userId, CancellationToken ct);

    /// <summary>Replaces every permission override of a MOD in the current Unit of Work.</summary>
    Task ReplaceModOverridesAsync(
        int userId,
        int grantedBy,
        DateTime grantedAt,
        IReadOnlyList<PermissionOverrideChange> overrides,
        CancellationToken ct);

    /// <summary>Replaces every default permission of the MOD role in the current Unit of Work.</summary>
    Task ReplaceModRoleDefaultsAsync(
        short modRoleId,
        IReadOnlyList<short> permissionIds,
        CancellationToken ct);
}
