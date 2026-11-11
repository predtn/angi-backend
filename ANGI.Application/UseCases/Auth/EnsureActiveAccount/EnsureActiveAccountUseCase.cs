using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Auth;

namespace ANGI.Application.UseCases.Auth.EnsureActiveAccount
{
    /// <summary>
    /// Blocks an account that is no longer active even while its access token is still valid
    /// (API Design, column Auth / Role).
    /// </summary>
    public sealed class EnsureActiveAccountUseCase : IEnsureActiveAccountUseCase
    {
        private readonly IAccountStatusCache _accountStatusCache;

        /// <summary>Initializes the use case with the cached status reader.</summary>
        public EnsureActiveAccountUseCase(IAccountStatusCache accountStatusCache)
        {
            _accountStatusCache = accountStatusCache;
        }

        /// <summary>Throws UNAUTHORIZED for a missing or deleted user and the AUTH-04 error for an inactive one.</summary>
        public async Task ExecuteAsync(int userId, CancellationToken ct)
        {
            var account = await _accountStatusCache.GetAsync(userId, ct)
                ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để thực hiện thao tác này.");

            AccountStatusRules.EnsureActive(account.Status, account.SuspendedUntil);
        }
    }
}
