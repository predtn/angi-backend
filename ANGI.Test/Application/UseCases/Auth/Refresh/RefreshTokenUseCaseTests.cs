using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Auth.Refresh;
using ANGI.Application.UseCases.Validators.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.Test.Application.UseCases.Auth.Refresh
{
    public sealed class RefreshTokenUseCaseTests
    {
        private static readonly DateTime Now = new(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);

        // TEST-01: Revoke the current session and persist a new session during token rotation.
        [Fact]
        public async Task ExecuteAsync_WithActiveSession_ShouldRotateRefreshToken()
        {
            var session = CreateSession();
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
            UserSession? addedSession = null;
            repository.Setup(x => x.AddSession(It.IsAny<UserSession>()))
                .Callback<UserSession>(value => addedSession = value);
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);
            var recommendationService = new Mock<IRecommendationService>();
            recommendationService.Setup(x => x.GetSurveyCompletionAsync(12, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var useCase = CreateUseCase(repository, recommendationService, unitOfWork);

            var result = await useCase.ExecuteAsync(
                new RefreshTokenRequestDto { RefreshToken = "old-token" },
                CancellationToken.None);

            session.RevokedAt.Should().Be(Now);
            addedSession.Should().NotBeNull();
            addedSession!.RefreshTokenHash.Should().Be("new-hash");
            result.RefreshToken.Should().Be("new-token");
            result.User.NeedsPreferenceSurvey.Should().BeFalse();
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-02: Revoke every active session when a previously revoked refresh token is reused.
        [Fact]
        public async Task ExecuteAsync_WithReusedToken_ShouldRevokeActiveSessionsAndRejectRequest()
        {
            var reusedSession = CreateSession();
            reusedSession.RevokedAt = Now.AddMinutes(-1);
            var activeSession = CreateSession();
            activeSession.Id = 2;
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(reusedSession);
            repository.Setup(x => x.GetActiveSessionsAsync(12, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<UserSession> { activeSession });
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            var useCase = CreateUseCase(repository, new Mock<IRecommendationService>(), unitOfWork);

            var act = () => useCase.ExecuteAsync(
                new RefreshTokenRequestDto { RefreshToken = "old-token" },
                CancellationToken.None);

            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("REFRESH_TOKEN_INVALID");
            activeSession.RevokedAt.Should().Be(Now);
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-03: Reject an unknown refresh token without writing to the database.
        [Fact]
        public async Task ExecuteAsync_WithUnknownToken_ShouldRejectWithoutSaving()
        {
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync((UserSession?)null);
            var unitOfWork = new Mock<IUnitOfWork>();
            var useCase = CreateUseCase(repository, new Mock<IRecommendationService>(), unitOfWork);

            var act = () => useCase.ExecuteAsync(
                new RefreshTokenRequestDto { RefreshToken = "old-token" },
                CancellationToken.None);

            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("REFRESH_TOKEN_INVALID");
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-04: Revoke and reject an expired refresh-token session without issuing replacement tokens.
        [Fact]
        public async Task ExecuteAsync_WithExpiredSession_ShouldRevokeAndRejectRequest()
        {
            var session = CreateSession();
            session.ExpiresAt = Now;
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            var useCase = CreateUseCase(repository, new Mock<IRecommendationService>(), unitOfWork);

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("REFRESH_TOKEN_INVALID");
            session.RevokedAt.Should().Be(Now);
            repository.Verify(x => x.AddSession(It.IsAny<UserSession>()), Times.Never);
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-05: Reject refresh for a non-active user and revoke the presented session.
        [Fact]
        public async Task ExecuteAsync_WithInactiveUser_ShouldRevokeAndRejectRequest()
        {
            var session = CreateSession();
            session.User.Status = UserStatus.Banned;
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            var useCase = CreateUseCase(repository, new Mock<IRecommendationService>(), unitOfWork);

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("REFRESH_TOKEN_INVALID");
            session.RevokedAt.Should().Be(Now);
        }

        // TEST-06: Stop before hashing or database access when the refresh request is empty.
        [Fact]
        public async Task ExecuteAsync_WithEmptyToken_ShouldThrowValidationExceptionFirst()
        {
            var repository = new Mock<IAuthenticationRepository>();
            var unitOfWork = new Mock<IUnitOfWork>();
            var tokenService = new Mock<IAuthenticationTokenService>();
            var useCase = new RefreshTokenUseCase(
                new RefreshTokenRequestDtoValidator(),
                repository.Object,
                tokenService.Object,
                new Mock<IRecommendationService>().Object,
                unitOfWork.Object,
                new FixedTimeProvider(Now));

            var act = () => useCase.ExecuteAsync(new RefreshTokenRequestDto(), CancellationToken.None);

            await act.Should().ThrowAsync<ValidationException>();
            tokenService.Verify(x => x.HashRefreshToken(It.IsAny<string>()), Times.Never);
            unitOfWork.Verify(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-07: Use a transaction and a row-locking repository query for single-use token rotation.
        [Fact]
        public async Task ExecuteAsync_WithActiveSession_ShouldUseLockedTransactionAndCommit()
        {
            var session = CreateSession();
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);
            var transaction = new Mock<IUnitOfWorkTransaction>();
            var useCase = CreateUseCase(
                repository,
                new Mock<IRecommendationService>(),
                unitOfWork,
                transaction);

            await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            unitOfWork.Verify(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Once);
            repository.Verify(
                x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()),
                Times.Once);
            repository.Verify(
                x => x.GetSessionByRefreshTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
            transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-08: Leave the refresh transaction uncommitted when persistence fails during rotation.
        [Fact]
        public async Task ExecuteAsync_WhenSaveFails_ShouldNotCommitTransaction()
        {
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateSession());
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Persistence failed."));
            var transaction = new Mock<IUnitOfWorkTransaction>();
            var useCase = CreateUseCase(
                repository,
                new Mock<IRecommendationService>(),
                unitOfWork,
                transaction);

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();
            transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-09: Allow only one success when concurrent requests reuse the same refresh token.
        [Fact]
        public async Task ExecuteAsync_WithConcurrentReuse_ShouldAllowOnlyOneSuccessfulRotation()
        {
            var originalSession = CreateSession();
            UserSession? replacementSession = null;
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(originalSession);
            repository.Setup(x => x.AddSession(It.IsAny<UserSession>()))
                .Callback<UserSession>(session => replacementSession = session);
            repository.Setup(x => x.GetActiveSessionsAsync(12, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => replacementSession is null
                    ? new List<UserSession>()
                    : new List<UserSession> { replacementSession });
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            var useCase = CreateUseCase(repository, new Mock<IRecommendationService>(), unitOfWork);
            var transactionGate = new SemaphoreSlim(1, 1);
            unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
                .Returns<CancellationToken>(async ct =>
                {
                    await transactionGate.WaitAsync(ct);
                    return new SerializingTransaction(transactionGate);
                });

            var attempts = await Task.WhenAll(ExecuteAsync(), ExecuteAsync());

            attempts.Count(attempt => attempt.Result is not null).Should().Be(1);
            attempts.Count(attempt => attempt.Error is UnauthorizedException unauthorized &&
                                      unauthorized.ErrorCode == "REFRESH_TOKEN_INVALID").Should().Be(1);
            replacementSession.Should().NotBeNull();
            replacementSession!.RevokedAt.Should().Be(Now);

            async Task<(AuthResultDto? Result, Exception? Error)> ExecuteAsync()
            {
                try
                {
                    return (await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None), null);
                }
                catch (Exception exception)
                {
                    return (null, exception);
                }
            }
        }

        private static RefreshTokenUseCase CreateUseCase(
            Mock<IAuthenticationRepository> repository,
            Mock<IRecommendationService> recommendationService,
            Mock<IUnitOfWork> unitOfWork,
            Mock<IUnitOfWorkTransaction>? transaction = null)
        {
            transaction ??= new Mock<IUnitOfWorkTransaction>();
            unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(transaction.Object);
            var tokenService = new Mock<IAuthenticationTokenService>();
            tokenService.Setup(x => x.HashRefreshToken("old-token")).Returns("old-hash");
            tokenService.Setup(x => x.CreateTokens(It.IsAny<User>())).Returns(new AuthenticationTokens(
                "new-access-token",
                Now.AddMinutes(15),
                "new-token",
                "new-hash",
                Now.AddDays(30)));

            return new RefreshTokenUseCase(
                new RefreshTokenRequestDtoValidator(),
                repository.Object,
                tokenService.Object,
                recommendationService.Object,
                unitOfWork.Object,
                new FixedTimeProvider(Now));
        }

        private static RefreshTokenRequestDto ValidRequest()
        {
            return new RefreshTokenRequestDto { RefreshToken = "old-token" };
        }

        private static UserSession CreateSession()
        {
            return new UserSession
            {
                Id = 1,
                UserId = 12,
                RefreshTokenHash = "old-hash",
                ExpiresAt = Now.AddDays(1),
                User = new User
                {
                    Id = 12,
                    Email = "user@angi.test",
                    DisplayName = "Test User",
                    Status = UserStatus.Active,
                    Role = new Role { Id = 1, Code = "TRAVELER", Name = "Traveler" }
                }
            };
        }

        private sealed class FixedTimeProvider : TimeProvider
        {
            private readonly DateTimeOffset _now;

            public FixedTimeProvider(DateTime now)
            {
                _now = new DateTimeOffset(now);
            }

            public override DateTimeOffset GetUtcNow() => _now;
        }

        private sealed class SerializingTransaction : IUnitOfWorkTransaction
        {
            private readonly SemaphoreSlim _gate;
            private bool _released;

            public SerializingTransaction(SemaphoreSlim gate)
            {
                _gate = gate;
            }

            public Task CommitAsync(CancellationToken ct)
            {
                Release();
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                Release();
                return ValueTask.CompletedTask;
            }

            private void Release()
            {
                if (_released)
                {
                    return;
                }

                _released = true;
                _gate.Release();
            }
        }
    }
}
