using ANGI.Domain.Enums;

namespace ANGI.Application.Common.Models.Administration;

/// <summary>Contains the default and user-specific permission state of one MOD account.</summary>
public sealed record ModPermissionsSnapshot(
    int UserId,
    IReadOnlyList<string> RoleDefaults,
    IReadOnlyList<PermissionOverrideSnapshot> Overrides);

/// <summary>Contains one persisted user-specific permission override.</summary>
public sealed record PermissionOverrideSnapshot(short PermissionId, string PermissionCode, PermissionEffect Effect);

/// <summary>Describes one replacement override to persist for a MOD account.</summary>
public sealed record PermissionOverrideChange(short PermissionId, PermissionEffect Effect);
