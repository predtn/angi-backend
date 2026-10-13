using ANGI.Application.Common.Interfaces.Repositories.Account;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Account;

/// <summary>Implements persistence operations for user profile queries and updates.</summary>
public sealed class AccountRepository : IAccountRepository
{
    private const string ModRole = "MOD";
    private const string AdminRole = "ADMIN";

    private readonly ANGIContext _context;

    public AccountRepository(ANGIContext context)
    {
        _context = context;
    }

    public async Task<User?> GetProfileUserAsync(int userId, CancellationToken ct)
    {
        return await _context.Users
            .AsNoTracking()
            .Include(u => u.Role)
            .Include(u => u.AvatarMedia)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    public async Task<User?> GetUserForUpdateAsync(int userId, CancellationToken ct)
    {
        return await _context.Users
            .Include(u => u.Role)
            .Include(u => u.AvatarMedia)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    public async Task<IReadOnlyList<string>> GetLinkedProvidersAsync(int userId, CancellationToken ct)
    {
        var providers = await _context.UserExternalLogins
            .AsNoTracking()
            .Where(login => login.UserId == userId)
            .Select(login => login.Provider)
            .ToListAsync(ct);

        return providers
            .Select(provider => provider.ToString().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task<long?> GetOwnerRestaurantIdAsync(int userId, CancellationToken ct)
    {
        return await _context.Restaurants
            .AsNoTracking()
            .Where(r => r.OwnerId == userId && r.DeletedAt == null)
            .Select(r => (long?)r.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetEffectivePermissionsAsync(
        int userId,
        string roleCode,
        CancellationToken ct)
    {
        if (string.Equals(roleCode, AdminRole, StringComparison.OrdinalIgnoreCase))
        {
            return await _context.Permissions
                .AsNoTracking()
                .Select(p => p.Code)
                .ToListAsync(ct);
        }

        if (string.Equals(roleCode, ModRole, StringComparison.OrdinalIgnoreCase))
        {
            var defaults = await _context.RolePermissions
                .AsNoTracking()
                .Where(rp => rp.Role.Code == ModRole)
                .Select(rp => rp.Permission.Code)
                .ToListAsync(ct);

            var userOverrides = await _context.UserPermissions
                .AsNoTracking()
                .Where(up => up.UserId == userId)
                .Select(up => new { up.Permission.Code, up.Effect })
                .ToListAsync(ct);

            var allow = userOverrides
                .Where(o => o.Effect == PermissionEffect.Allow)
                .Select(o => o.Code);

            var deny = userOverrides
                .Where(o => o.Effect == PermissionEffect.Deny)
                .Select(o => o.Code)
                .ToHashSet(StringComparer.Ordinal);

            return defaults
                .Concat(allow)
                .Where(code => !deny.Contains(code))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        return [];
    }
}
