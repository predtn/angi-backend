using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Administration;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Administration;

/// <summary>Implements MOD account administration persistence with EF Core.</summary>
public sealed class AdministrationModRepository : IAdministrationModRepository
{
    private const string ModRoleCode = "MOD";
    private readonly ANGIContext _context;

    /// <summary>Initializes MOD account persistence with the scoped application context.</summary>
    public AdministrationModRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <summary>Filters, orders, projects, and pages MOD accounts without tracking entities.</summary>
    public async Task<Paged<AdminUserSnapshot>> GetModsAsync(
        string? query,
        UserStatus? status,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var users = _context.Users
            .AsNoTracking()
            .Where(user => user.Role.Code == ModRoleCode);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = $"%{EscapeLikePattern(query.Trim())}%";
            users = users.Where(user =>
                EF.Functions.ILike(user.Email, pattern, "\\") ||
                EF.Functions.ILike(user.DisplayName, pattern, "\\"));
        }

        if (status.HasValue)
        {
            users = users.Where(user => user.Status == status.Value);
        }

        var totalItems = await users.LongCountAsync(ct);
        var offset = ((long)page - 1) * pageSize;
        IReadOnlyList<AdminUserSnapshot> items = offset > int.MaxValue
            ? []
            : await users
                .OrderByDescending(user => user.CreatedAt)
                .ThenByDescending(user => user.Id)
                .Skip((int)offset)
                .Take(pageSize)
                .Select(user => new AdminUserSnapshot(
                    user.Id,
                    user.Email,
                    user.DisplayName,
                    user.AvatarMedia == null ? null : user.AvatarMedia.Url,
                    user.Role.Code,
                    user.Status,
                    user.SuspendedUntil,
                    user.LastLoginAt,
                    user.CreatedAt))
                .ToListAsync(ct);

        return new Paged<AdminUserSnapshot>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalItems = totalItems,
            TotalPages = totalItems == 0
                ? 0
                : (int)Math.Min(int.MaxValue, (totalItems + pageSize - 1) / pageSize)
        };
    }

    /// <summary>Projects one non-deleted MOD account without loading unrelated columns.</summary>
    public Task<AdminUserSnapshot?> GetModAsync(int userId, CancellationToken ct)
    {
        return _context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId && user.Role.Code == ModRoleCode)
            .Select(user => new AdminUserSnapshot(
                user.Id,
                user.Email,
                user.DisplayName,
                user.AvatarMedia == null ? null : user.AvatarMedia.Url,
                user.Role.Code,
                user.Status,
                user.SuspendedUntil,
                user.LastLoginAt,
                user.CreatedAt))
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>Locks the target row and then loads its tracked MOD entity for mutation.</summary>
    public async Task<User?> GetModForUpdateAsync(int userId, CancellationToken ct)
    {
        await _context.Database.ExecuteSqlInterpolatedAsync($$"""
            SELECT id
            FROM core.users
            WHERE id = {{userId}}
              AND role_id = (SELECT id FROM core.roles WHERE code = 'MOD')
              AND deleted_at IS NULL
            FOR UPDATE
            """, ct);

        return await _context.Users
            .Include(user => user.Role)
            .Include(user => user.AvatarMedia)
            .SingleOrDefaultAsync(user => user.Id == userId && user.Role.Code == ModRoleCode, ct);
    }

    /// <summary>Counts all rows attributed to the MOD reviewer or handler identifiers.</summary>
    public async Task<ModHandledSnapshot> GetHandledAsync(int userId, CancellationToken ct)
    {
        var verifications = await _context.RestaurantVerifications
            .AsNoTracking()
            .CountAsync(item => item.ReviewedBy == userId, ct);
        var menus = await _context.MenuSubmissions
            .AsNoTracking()
            .CountAsync(item => item.ReviewedBy == userId, ct);
        var reports = await _context.Reports
            .AsNoTracking()
            .CountAsync(item => item.HandledBy == userId, ct);
        return new ModHandledSnapshot(verifications, menus, reports);
    }

    /// <summary>Checks the same soft-delete-aware email domain protected by the unique index.</summary>
    public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken ct) =>
        _context.Users.AsNoTracking().AnyAsync(user => user.Email == normalizedEmail, ct);

    /// <summary>Adds a new MOD entity to the current change tracker.</summary>
    public void AddMod(User user) => _context.Users.Add(user);

    /// <summary>Adds the new MOD's password-reset invitation token to the current change tracker.</summary>
    public void AddPasswordResetToken(UserToken token) => _context.UserTokens.Add(token);

    /// <summary>Adds initial MOD permission overrides to the current change tracker.</summary>
    public void AddPermissionOverrides(IEnumerable<UserPermission> overrides) =>
        _context.UserPermissions.AddRange(overrides);

    /// <summary>Loads every unrevoked session belonging to the selected MOD.</summary>
    public async Task<IReadOnlyList<UserSession>> GetActiveSessionsAsync(int userId, CancellationToken ct) =>
        await _context.UserSessions
            .Where(session => session.UserId == userId && session.RevokedAt == null)
            .ToListAsync(ct);

    /// <summary>Escapes PostgreSQL LIKE metacharacters so the search term remains literal.</summary>
    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
