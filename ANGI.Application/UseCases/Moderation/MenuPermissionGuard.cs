using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services.Auth;

namespace ANGI.Application.UseCases.Moderation;

/// <summary>Enforces authenticated MOD/ADMIN access and effective MENU_APPROVE permission.</summary>
internal static class MenuPermissionGuard
{
    /// <summary>Returns the authorized caller identifier or raises a standard access error.</summary>
    public static async Task<int> RequireAsync(ICurrentUserService currentUser, IMenuReviewRepository repository, CancellationToken ct)
    {
        // STEP-01: Prepare userId.
        var userId = currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để duyệt menu.");
        // STEP-02: Reject with FORBIDDEN when currentUser.Role is not ("MOD" or "ADMIN") or repository.HasPermissionAsync() is false.
        if (currentUser.Role is not ("MOD" or "ADMIN") || !await repository.HasPermissionAsync(userId, ct))
            throw new ForbiddenException("FORBIDDEN", "Bạn không có quyền duyệt menu nhà hàng.");
        // STEP-03: Return userId.
        return userId;
    }
}
