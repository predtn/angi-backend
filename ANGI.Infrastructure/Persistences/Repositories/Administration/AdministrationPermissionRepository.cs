using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Models.Administration;
using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Administration;

/// <summary>Implements permission administration persistence with EF Core.</summary>
public sealed class AdministrationPermissionRepository : IAdministrationPermissionRepository
{
    private const string ModRoleCode = "MOD";
    private readonly ANGIContext _context;

    /// <summary>Initializes permission administration persistence with the application context.</summary>
    public AdministrationPermissionRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <summary>Loads the ordered permission catalog and current MOD defaults without tracking.</summary>
    public async Task<PermissionCatalogSnapshot> GetCatalogAsync(CancellationToken ct)
    {
        var modRoleId = await _context.Roles
            .AsNoTracking()
            .Where(role => role.Code == ModRoleCode)
            .Select(role => (short?)role.Id)
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The seeded MOD role is missing.");
        var permissions = await _context.Permissions
            .AsNoTracking()
            .OrderBy(permission => permission.Id)
            .Select(permission => new PermissionDefinition(
                permission.Id,
                permission.Code,
                permission.Description))
            .ToListAsync(ct);
        var modDefaults = await _context.RolePermissions
            .AsNoTracking()
            .Where(item => item.RoleId == modRoleId)
            .OrderBy(item => item.PermissionId)
            .Select(item => item.Permission.Code)
            .ToListAsync(ct);

        return new PermissionCatalogSnapshot(modRoleId, permissions, modDefaults);
    }

    /// <summary>Loads an active MOD and its ordered default and override permission state.</summary>
    public async Task<ModPermissionsSnapshot?> GetModPermissionsAsync(int userId, CancellationToken ct)
    {
        var mod = await _context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId && user.Role.Code == ModRoleCode)
            .Select(user => new { user.Id, user.RoleId })
            .SingleOrDefaultAsync(ct);
        if (mod is null)
        {
            return null;
        }

        var defaults = await _context.RolePermissions
            .AsNoTracking()
            .Where(item => item.RoleId == mod.RoleId)
            .OrderBy(item => item.PermissionId)
            .Select(item => item.Permission.Code)
            .ToListAsync(ct);
        var overrides = await _context.UserPermissions
            .AsNoTracking()
            .Where(item => item.UserId == userId)
            .OrderBy(item => item.PermissionId)
            .Select(item => new PermissionOverrideSnapshot(
                item.PermissionId,
                item.Permission.Code,
                item.Effect))
            .ToListAsync(ct);

        return new ModPermissionsSnapshot(mod.Id, defaults, overrides);
    }

    /// <summary>Removes existing MOD overrides and tracks the supplied complete replacement set.</summary>
    public async Task ReplaceModOverridesAsync(
        int userId,
        int grantedBy,
        DateTime grantedAt,
        IReadOnlyList<PermissionOverrideChange> overrides,
        CancellationToken ct)
    {
        var existing = await _context.UserPermissions
            .Where(item => item.UserId == userId)
            .ToListAsync(ct);
        var replacements = overrides.ToDictionary(item => item.PermissionId);
        _context.UserPermissions.RemoveRange(
            existing.Where(item => !replacements.ContainsKey(item.PermissionId)));

        foreach (var replacement in overrides)
        {
            var current = existing.FirstOrDefault(item => item.PermissionId == replacement.PermissionId);
            if (current is not null)
            {
                current.Effect = replacement.Effect;
                current.GrantedBy = grantedBy;
                current.GrantedAt = grantedAt;
                continue;
            }

            _context.UserPermissions.Add(new UserPermission
            {
                UserId = userId,
                PermissionId = replacement.PermissionId,
                Effect = replacement.Effect,
                GrantedBy = grantedBy,
                GrantedAt = grantedAt
            });
        }
    }

    /// <summary>Removes existing MOD role defaults and tracks the supplied complete replacement set.</summary>
    public async Task ReplaceModRoleDefaultsAsync(
        short modRoleId,
        IReadOnlyList<short> permissionIds,
        CancellationToken ct)
    {
        var existing = await _context.RolePermissions
            .Where(item => item.RoleId == modRoleId)
            .ToListAsync(ct);
        var replacementIds = permissionIds.ToHashSet();
        _context.RolePermissions.RemoveRange(
            existing.Where(item => !replacementIds.Contains(item.PermissionId)));
        var existingIds = existing.Select(item => item.PermissionId).ToHashSet();
        _context.RolePermissions.AddRange(permissionIds
            .Where(permissionId => !existingIds.Contains(permissionId))
            .Select(permissionId => new RolePermission
            {
                RoleId = modRoleId,
                PermissionId = permissionId
            }));
    }
}
