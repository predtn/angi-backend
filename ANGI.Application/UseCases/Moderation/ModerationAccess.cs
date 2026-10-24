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
        var userId = currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập.");
        if (currentUser.Role == "ADMIN") return userId;
        if (currentUser.Role == "MOD")
        {
            var effective = await accounts.GetEffectivePermissionsAsync(userId, "MOD", ct);
            if (permissions.Any(permission => effective.Contains(permission, StringComparer.Ordinal))) return userId;
        }
        throw new ForbiddenException("FORBIDDEN", "Bạn không có quyền thực hiện thao tác này.");
    }
}
