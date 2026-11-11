namespace ANGI.Application.Common.Models.Administration;

/// <summary>Contains the permission catalog and current MOD role defaults loaded from persistence.</summary>
public sealed record PermissionCatalogSnapshot(
    short ModRoleId,
    IReadOnlyList<PermissionDefinition> Permissions,
    IReadOnlyList<string> ModDefaults);

/// <summary>Identifies one persisted permission used by administration use cases.</summary>
public sealed record PermissionDefinition(short Id, string Code, string? Description);
