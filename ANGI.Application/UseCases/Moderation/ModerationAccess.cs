using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Account;
using ANGI.Application.Common.Interfaces.Services.Auth;

namespace ANGI.Application.UseCases.Moderation;

/// <summary>Checks moderator roles and current database permissions for moderation use cases.</summary>
public sealed class ModerationAccess(ICurrentUserService currentUser, IAccountRepository accounts)
{
    /// <summary>Allows Admin or a Mod with at least one of the requested effective permissions.</summary>
    public async Task<int> RequireAsync(CancellationToken ct, params string[] permissions)
    {
        // STEP-01: Prepare userId.
        var userId = currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập.");
        // STEP-02: Return userId early when currentUser.Role is "ADMIN".
        if (currentUser.Role == "ADMIN") return userId;
        // STEP-03: When currentUser.Role is "MOD" or "MODERATOR", prepare effective through accounts.GetEffectivePermissionsAsync and more.
        if (currentUser.Role is "MOD" or "MODERATOR")
        {
            var effective = await accounts.GetEffectivePermissionsAsync(userId, "MOD", ct);
            if (permissions.Any(permission => effective.Contains(permission, StringComparer.Ordinal))) return userId;
        }
        // STEP-04: Reject with FORBIDDEN.
        throw new ForbiddenException("FORBIDDEN", "Bạn không có quyền thực hiện thao tác này.");
    }
}
