using ANGI.Application.Common.Exceptions;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Auth
{
    /// <summary>Maps a user status to the AUTH-04 error, shared by login and the per-request account check.</summary>
    internal static class AccountStatusRules
    {
        /// <summary>Returns for an Active account and throws the matching error code for any other status.</summary>
        public static void EnsureActive(UserStatus status, DateTime? suspendedUntil)
        {
            switch (status)
            {
                case UserStatus.Active:
                    return;
                case UserStatus.PendingVerification:
                    throw new ForbiddenException("EMAIL_NOT_VERIFIED", "Email chưa được xác minh.");
                case UserStatus.Suspended:
                    throw new AccountSuspendedException(suspendedUntil);
                case UserStatus.Banned:
                    throw new ForbiddenException("ACCOUNT_BANNED", "Tài khoản đã bị cấm.");
                case UserStatus.Deactivated:
                    throw new ForbiddenException("ACCOUNT_DEACTIVATED", "Tài khoản đã bị vô hiệu hóa.");
                default:
                    throw new ForbiddenException("ACCOUNT_DEACTIVATED", "Tài khoản không thể đăng nhập.");
            }
        }
    }
}
