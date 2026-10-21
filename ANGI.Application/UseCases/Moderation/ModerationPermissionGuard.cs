using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services.Auth;

namespace ANGI.Application.UseCases.Moderation;

/// <summary>Centralizes effective-permission checks shared by moderation use cases.</summary>
internal static class ModerationPermissionGuard
{
    public const string RestaurantVerify = "RESTAURANT_VERIFY";

    /// <summary>Returns the authenticated user id or raises the standard authorization errors.</summary>
    public static async Task<int> RequireAsync(
        ICurrentUserService currentUserService,
        IVerificationReviewRepository repository,
        CancellationToken ct)
    {
        var userId = currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để thực hiện thao tác này.");

        if (currentUserService.Role != "MOD" ||
            !await repository.HasEffectivePermissionAsync(userId, RestaurantVerify, ct))
        {
            throw new ForbiddenException("FORBIDDEN", "Bạn không có quyền duyệt hồ sơ xác minh nhà hàng.");
        }

        return userId;
    }
}
