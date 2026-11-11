using ANGI.Application.Common.Models.Administration;
using ANGI.Application.DTOs.Administration;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Administration;

/// <summary>Maps persistence snapshots to the permission administration API contracts.</summary>
internal static class AdministrationPermissionMapper
{
    /// <summary>Maps the catalog snapshot to ADM-P1 while preserving the documented uppercase role keys.</summary>
    public static PermissionCatalogDto ToCatalogDto(PermissionCatalogSnapshot snapshot) => new()
    {
        Permissions = snapshot.Permissions
            .Select(permission => new PermissionDto
            {
                Code = permission.Code,
                Description = permission.Description ?? string.Empty
            })
            .ToArray(),
        RoleDefaults = new RolePermissionDefaultsDto
        {
            Mod = snapshot.ModDefaults,
            Admin = ["*"]
        }
    };

    /// <summary>Maps role defaults and overrides and calculates effective permissions.</summary>
    public static ModPermissionsDto ToModPermissionsDto(
        IReadOnlyList<string> roleDefaults,
        IReadOnlyList<PermissionOverrideSnapshot> overrides)
    {
        var denied = overrides
            .Where(item => item.Effect == PermissionEffect.Deny)
            .Select(item => item.PermissionCode)
            .ToHashSet(StringComparer.Ordinal);
        var allowed = overrides
            .Where(item => item.Effect == PermissionEffect.Allow)
            .Select(item => item.PermissionCode);

        return new ModPermissionsDto
        {
            RoleDefaults = roleDefaults,
            Overrides = overrides.Select(item => new PermissionOverrideDto
            {
                PermissionCode = item.PermissionCode,
                Effect = item.Effect == PermissionEffect.Allow ? "allow" : "deny"
            }).ToArray(),
            Effective = roleDefaults
                .Concat(allowed)
                .Where(code => !denied.Contains(code))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }
}
