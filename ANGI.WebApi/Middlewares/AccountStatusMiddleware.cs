using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using Microsoft.AspNetCore.Authorization;

namespace ANGI.WebApi.Middlewares
{
    /// <summary>
    /// On endpoints that need a token, rejects an account that is no longer active even when its access
    /// token is still valid. Must run after authorization so a missing token is answered with 401 first.
    /// </summary>
    public sealed class AccountStatusMiddleware : IMiddleware
    {
        private readonly IEnsureActiveAccountUseCase _ensureActiveAccount;
        private readonly ICurrentUserService _currentUserService;

        /// <summary>Initializes the middleware with the account check and the current user.</summary>
        public AccountStatusMiddleware(
            IEnsureActiveAccountUseCase ensureActiveAccount,
            ICurrentUserService currentUserService)
        {
            _ensureActiveAccount = ensureActiveAccount;
            _currentUserService = currentUserService;
        }

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if (RequiresToken(context)
                && context.User.Identity?.IsAuthenticated == true
                && _currentUserService.UserId is int userId)
            {
                await _ensureActiveAccount.ExecuteAsync(userId, context.RequestAborted);
            }

            await next(context);
        }

        /// <summary>True for [Authorize] endpoints; public and [AllowAnonymous] endpoints are not checked.</summary>
        private static bool RequiresToken(HttpContext context)
        {
            var metadata = context.GetEndpoint()?.Metadata;
            return metadata?.GetMetadata<IAuthorizeData>() is not null
                && metadata.GetMetadata<IAllowAnonymous>() is null;
        }
    }
}
