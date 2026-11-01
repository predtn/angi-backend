using System.Security.Claims;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.WebApi.Middlewares;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Moq;

namespace ANGI.UnitTests.WebApi.Middlewares
{
    /// <summary>Verifies AccountStatusMiddleware checks the account status only on authorized endpoints for signed-in users.</summary>
    public sealed class AccountStatusMiddlewareTests
    {
        private readonly Mock<IEnsureActiveAccountUseCase> _ensureActiveAccount = new();
        private readonly Mock<ICurrentUserService> _currentUser = new();

        public AccountStatusMiddlewareTests()
        {
            _currentUser.Setup(x => x.UserId).Returns(12);
        }

        // TEST-01: Check the account on an [Authorize] endpoint and continue when it is active.
        /// <summary>Ensures an active account passes through protected endpoints.</summary>
        [Fact]
        public async Task InvokeAsync_OnAuthorizedEndpoint_ShouldCheckAccountAndContinue()
        {
            var context = CreateContext(authenticated: true, new AuthorizeAttribute());
            var nextCalled = false;

            await CreateMiddleware().InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

            _ensureActiveAccount.Verify(x => x.ExecuteAsync(12, It.IsAny<CancellationToken>()), Times.Once);
            nextCalled.Should().BeTrue();
        }

        // TEST-02: Stop the request when the account check throws (e.g. banned account).
        /// <summary>Ensures a blocked account cannot reach protected endpoints with an old token.</summary>
        [Fact]
        public async Task InvokeAsync_WithInactiveAccount_ShouldThrowAndNotCallNext()
        {
            _ensureActiveAccount.Setup(x => x.ExecuteAsync(12, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ForbiddenException("ACCOUNT_BANNED", "Tài khoản đã bị cấm."));
            var context = CreateContext(authenticated: true, new AuthorizeAttribute());
            var nextCalled = false;

            var act = () => CreateMiddleware().InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });

            await act.Should().ThrowAsync<ForbiddenException>();
            nextCalled.Should().BeFalse();
        }

        // TEST-03: Skip [AllowAnonymous] and public endpoints even when a token is sent.
        /// <summary>Avoids a status lookup on endpoints that do not require sign-in.</summary>
        [Fact]
        public async Task InvokeAsync_OnAnonymousOrPublicEndpoint_ShouldNotCheckAccount()
        {
            var middleware = CreateMiddleware();

            await middleware.InvokeAsync(CreateContext(true, new AuthorizeAttribute(), new AllowAnonymousAttribute()), _ => Task.CompletedTask);
            await middleware.InvokeAsync(CreateContext(true), _ => Task.CompletedTask);

            _ensureActiveAccount.Verify(x => x.ExecuteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-04: Leave unauthenticated requests to the authorization middleware (401).
        /// <summary>Leaves anonymous requests to the authorization middleware so they get 401.</summary>
        [Fact]
        public async Task InvokeAsync_WithoutAuthenticatedUser_ShouldNotCheckAccount()
        {
            await CreateMiddleware().InvokeAsync(CreateContext(false, new AuthorizeAttribute()), _ => Task.CompletedTask);

            _ensureActiveAccount.Verify(x => x.ExecuteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        /// <summary>Creates the middleware over the shared account check and current-user mocks.</summary>
        private AccountStatusMiddleware CreateMiddleware()
        {
            return new AccountStatusMiddleware(_ensureActiveAccount.Object, _currentUser.Object);
        }

        /// <summary>Creates an HTTP context, signed in or not, whose endpoint carries the given metadata.</summary>
        private static DefaultHttpContext CreateContext(bool authenticated, params object[] endpointMetadata)
        {
            var context = new DefaultHttpContext
            {
                User = authenticated
                    ? new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "12")], "Bearer"))
                    : new ClaimsPrincipal(new ClaimsIdentity())
            };
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(endpointMetadata), "test"));
            return context;
        }
    }
}
