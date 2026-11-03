using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Auth.Logout;
using ANGI.Application.UseCases.Validators.Auth;
using ANGI.Domain.Entities;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Auth.Logout
{
    /// <summary>Verifies LogoutUseCase revokes only the caller's own session and stays idempotent for every other token.</summary>
    public sealed class LogoutUseCaseTests
    {
        private static readonly DateTime Now = new(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);

        // TEST-01: Revoke the refresh-token session that belongs to the authenticated user.
        /// <summary>Ensures logout ends the caller's refresh-token session.</summary>
        [Fact]
        public async Task ExecuteAsync_WithOwnedSession_ShouldRevokeSession()
        {
            var session = new UserSession { UserId = 12, RefreshTokenHash = "token-hash" };
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashAsync("token-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            var useCase = CreateUseCase(repository, unitOfWork, 12);

            await useCase.ExecuteAsync(
                new LogoutRequestDto { RefreshToken = "refresh-token" },
                CancellationToken.None);

            session.RevokedAt.Should().Be(Now);
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-02: Keep logout idempotent when the token does not belong to the authenticated user.
        /// <summary>Prevents one user from revoking another user's session through logout.</summary>
        [Fact]
        public async Task ExecuteAsync_WithAnotherUsersSession_ShouldNotModifySession()
        {
            var session = new UserSession { UserId = 99, RefreshTokenHash = "token-hash" };
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashAsync("token-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
            var unitOfWork = new Mock<IUnitOfWork>();
            var useCase = CreateUseCase(repository, unitOfWork, 12);

            await useCase.ExecuteAsync(
                new LogoutRequestDto { RefreshToken = "refresh-token" },
                CancellationToken.None);

            session.RevokedAt.Should().BeNull();
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-03: Keep logout idempotent when the supplied refresh token is unknown.
        /// <summary>Keeps logout idempotent and free of writes for an unknown token.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUnknownToken_ShouldReturnWithoutSaving()
        {
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashAsync("token-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync((UserSession?)null);
            var unitOfWork = new Mock<IUnitOfWork>();
            var useCase = CreateUseCase(repository, unitOfWork, 12);

            await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-04: Keep logout idempotent when the matching session has already been revoked.
        /// <summary>Keeps repeated logouts idempotent and free of writes.</summary>
        [Fact]
        public async Task ExecuteAsync_WithRevokedSession_ShouldReturnWithoutSaving()
        {
            var session = new UserSession
            {
                UserId = 12,
                RefreshTokenHash = "token-hash",
                RevokedAt = Now.AddMinutes(-1)
            };
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashAsync("token-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
            var unitOfWork = new Mock<IUnitOfWork>();
            var useCase = CreateUseCase(repository, unitOfWork, 12);

            await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            session.RevokedAt.Should().Be(Now.AddMinutes(-1));
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-05: Stop before token hashing and repository access when the logout request is empty.
        /// <summary>Ensures an empty request is rejected before hashing or database access.</summary>
        [Fact]
        public async Task ExecuteAsync_WithEmptyToken_ShouldThrowValidationExceptionFirst()
        {
            var repository = new Mock<IAuthenticationRepository>();
            var unitOfWork = new Mock<IUnitOfWork>();
            var tokenService = new Mock<IAuthenticationTokenService>();
            var currentUserService = new Mock<ICurrentUserService>();
            var useCase = new LogoutUseCase(
                new LogoutRequestDtoValidator(),
                repository.Object,
                tokenService.Object,
                currentUserService.Object,
                unitOfWork.Object,
                new FixedTimeProvider(Now));

            var act = () => useCase.ExecuteAsync(new LogoutRequestDto(), CancellationToken.None);

            await act.Should().ThrowAsync<ValidationException>();
            tokenService.Verify(x => x.HashRefreshToken(It.IsAny<string>()), Times.Never);
            repository.Verify(
                x => x.GetSessionByRefreshTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>Creates the use case for the given user with a token service that hashes the default refresh token.</summary>
        private static LogoutUseCase CreateUseCase(
            Mock<IAuthenticationRepository> repository,
            Mock<IUnitOfWork> unitOfWork,
            int userId)
        {
            var tokenService = new Mock<IAuthenticationTokenService>();
            tokenService.Setup(x => x.HashRefreshToken("refresh-token")).Returns("token-hash");
            var currentUserService = new Mock<ICurrentUserService>();
            currentUserService.SetupGet(x => x.UserId).Returns(userId);

            return new LogoutUseCase(
                new LogoutRequestDtoValidator(),
                repository.Object,
                tokenService.Object,
                currentUserService.Object,
                unitOfWork.Object,
                new FixedTimeProvider(Now));
        }

        /// <summary>Creates a logout request carrying the default refresh token.</summary>
        private static LogoutRequestDto ValidRequest()
        {
            return new LogoutRequestDto { RefreshToken = "refresh-token" };
        }

        /// <summary>Clock fixed at one instant so revocation timestamps can be asserted exactly.</summary>
        private sealed class FixedTimeProvider : TimeProvider
        {
            private readonly DateTimeOffset _now;

            public FixedTimeProvider(DateTime now)
            {
                _now = new DateTimeOffset(now);
            }

            /// <summary>Returns the fixed instant supplied to the constructor.</summary>
            public override DateTimeOffset GetUtcNow() => _now;
        }
    }
}
