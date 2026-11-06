using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Auth;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Auth.Refresh;
using ANGI.Application.UseCases.Validators.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Auth.Refresh
{
    /// <summary>Verifies RefreshTokenUseCase rotates refresh tokens once, detects reuse and rejects expired or blocked sessions.</summary>
    public sealed class RefreshTokenUseCaseTests
    {
        private static readonly DateTime Now = new(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);

        // TEST-01: Revoke the current session, persist a new session, and issue the access token with the new session id.
        /// <summary>Ensures refresh replaces the session and binds the new access token to the new session id.</summary>
        [Fact]
        public async Task ExecuteAsync_WithActiveSession_ShouldRotateRefreshToken()
        {
            // STEP-01: Create session through CreateSession.
            var session = CreateSession();
            // STEP-02: Declare addedSession to capture a call argument; mock IAuthenticationRepository and 2 more; stub repository.GetSessionByRefreshTokenHashForUpdateAsync and 3 more.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
            UserSession? addedSession = null;
            repository.Setup(x => x.AddSession(It.IsAny<UserSession>()))
                .Callback<UserSession>(value => addedSession = value);
            var unitOfWork = new Mock<IUnitOfWork>();
            // The database assigns the new session id on save.
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .Callback(() => addedSession!.Id = 78)
                .ReturnsAsync(2);
            var recommendationService = new Mock<IRecommendationService>();
            recommendationService.Setup(x => x.GetSurveyStatusAsync(12, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SurveyStatus(true, new DateTime(2026, 10, 2, 2, 30, 1, DateTimeKind.Utc), 6));
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, recommendationService, unitOfWork);

            // STEP-04: Run useCase.ExecuteAsync with RefreshTokenRequestDto { RefreshToken = a test value } and keep result.
            var result = await useCase.ExecuteAsync(
                new RefreshTokenRequestDto { RefreshToken = "old-token" },
                CancellationToken.None);

            // STEP-05: Assert session.RevokedAt is Now, addedSession is not null and addedSession.RefreshTokenHash is a test value.
            session.RevokedAt.Should().Be(Now);
            addedSession.Should().NotBeNull();
            addedSession!.RefreshTokenHash.Should().Be("new-hash");
            // The link tells a late duplicate of this token (concurrent refresh) from a logout.
            session.ReplacedBySession.Should().BeSameAs(addedSession);
            // STEP-06: Assert result.RefreshToken is a test value, result.AccessToken is a test value and result.User.NeedsPreferenceSurvey is false.
            result.RefreshToken.Should().Be("new-token");
            // sid names the new session, not the revoked one.
            result.AccessToken.Should().Be("new-access-token-sid-78");
            result.User.NeedsPreferenceSurvey.Should().BeFalse();
            // STEP-07: Verify SaveChangesAsync is called once.
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-02: Revoke every active session when a previously revoked refresh token is reused.
        /// <summary>Treats reuse of a revoked token as theft and ends every active session.</summary>
        [Fact]
        public async Task ExecuteAsync_WithReusedToken_ShouldRevokeActiveSessionsAndRejectRequest()
        {
            // STEP-01: Create reusedSession through CreateSession, set reusedSession.RevokedAt to Now.AddMinutes(), create activeSession through CreateSession and 1 more.
            var reusedSession = CreateSession();
            reusedSession.RevokedAt = Now.AddMinutes(-1);
            var activeSession = CreateSession();
            activeSession.Id = 2;
            // STEP-02: Mock IAuthenticationRepository and 1 more; stub repository.GetSessionByRefreshTokenHashForUpdateAsync and 2 more.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(reusedSession);
            repository.Setup(x => x.GetActiveSessionsAsync(12, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<UserSession> { activeSession });
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, new Mock<IRecommendationService>(), unitOfWork);

            // STEP-04: Wrap useCase.ExecuteAsync with RefreshTokenRequestDto { RefreshToken = a test value } in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(
                new RefreshTokenRequestDto { RefreshToken = "old-token" },
                CancellationToken.None);

            // STEP-05: Assert act throws UnauthorizedException, exception.ErrorCode is "REFRESH_TOKEN_INVALID" and activeSession.RevokedAt is Now.
            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("REFRESH_TOKEN_INVALID");
            activeSession.RevokedAt.Should().Be(Now);
            // STEP-06: Verify SaveChangesAsync is called once.
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-03: Reject an unknown refresh token without writing to the database.
        /// <summary>Ensures an unknown token is rejected without any write.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUnknownToken_ShouldRejectWithoutSaving()
        {
            // STEP-01: Mock IAuthenticationRepository and IUnitOfWork; stub repository.GetSessionByRefreshTokenHashForUpdateAsync.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync((UserSession?)null);
            var unitOfWork = new Mock<IUnitOfWork>();
            // STEP-02: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, new Mock<IRecommendationService>(), unitOfWork);

            // STEP-03: Wrap useCase.ExecuteAsync with RefreshTokenRequestDto { RefreshToken = a test value } in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(
                new RefreshTokenRequestDto { RefreshToken = "old-token" },
                CancellationToken.None);

            // STEP-04: Assert act throws UnauthorizedException and exception.ErrorCode is "REFRESH_TOKEN_INVALID".
            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("REFRESH_TOKEN_INVALID");
            // STEP-05: Verify SaveChangesAsync is never called.
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-04: Revoke and reject an expired refresh-token session without issuing replacement tokens.
        /// <summary>Ensures an expired session is closed and cannot mint new tokens.</summary>
        [Fact]
        public async Task ExecuteAsync_WithExpiredSession_ShouldRevokeAndRejectRequest()
        {
            // STEP-01: Create session through CreateSession and set session.ExpiresAt to Now.
            var session = CreateSession();
            session.ExpiresAt = Now;
            // STEP-02: Mock IAuthenticationRepository and IUnitOfWork; stub repository.GetSessionByRefreshTokenHashForUpdateAsync and unitOfWork.SaveChangesAsync.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, new Mock<IRecommendationService>(), unitOfWork);

            // STEP-04: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-05: Assert act throws UnauthorizedException, exception.ErrorCode is "REFRESH_TOKEN_INVALID" and session.RevokedAt is Now.
            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("REFRESH_TOKEN_INVALID");
            session.RevokedAt.Should().Be(Now);
            // STEP-06: Verify AddSession is never called and SaveChangesAsync is called once.
            repository.Verify(x => x.AddSession(It.IsAny<UserSession>()), Times.Never);
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-05: Reject refresh for a non-active user and revoke the presented session.
        /// <summary>Ensures a blocked account cannot keep its access alive through refresh.</summary>
        [Fact]
        public async Task ExecuteAsync_WithInactiveUser_ShouldRevokeAndRejectRequest()
        {
            // STEP-01: Create session through CreateSession and set session.User.Status to UserStatus.Banned.
            var session = CreateSession();
            session.User.Status = UserStatus.Banned;
            // STEP-02: Mock IAuthenticationRepository and IUnitOfWork; stub repository.GetSessionByRefreshTokenHashForUpdateAsync and unitOfWork.SaveChangesAsync.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, new Mock<IRecommendationService>(), unitOfWork);

            // STEP-04: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-05: Assert act throws UnauthorizedException, exception.ErrorCode is "REFRESH_TOKEN_INVALID" and session.RevokedAt is Now.
            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("REFRESH_TOKEN_INVALID");
            session.RevokedAt.Should().Be(Now);
        }

        // TEST-06: Stop before hashing or database access when the refresh request is empty.
        /// <summary>Ensures an empty request is rejected before hashing or database access.</summary>
        [Fact]
        public async Task ExecuteAsync_WithEmptyToken_ShouldThrowValidationExceptionFirst()
        {
            // STEP-01: Mock IAuthenticationRepository, IUnitOfWork and IAuthenticationTokenService.
            var repository = new Mock<IAuthenticationRepository>();
            var unitOfWork = new Mock<IUnitOfWork>();
            var tokenService = new Mock<IAuthenticationTokenService>();
            // STEP-02: Create the RefreshTokenUseCase under test as useCase.
            var useCase = new RefreshTokenUseCase(
                new RefreshTokenRequestDtoValidator(),
                repository.Object,
                tokenService.Object,
                new Mock<IRecommendationService>().Object,
                unitOfWork.Object,
                new FixedTimeProvider(Now));

            // STEP-03: Wrap useCase.ExecuteAsync with an empty RefreshTokenRequestDto in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(new RefreshTokenRequestDto(), CancellationToken.None);

            // STEP-04: Assert act throws ValidationException.
            await act.Should().ThrowAsync<ValidationException>();
            // STEP-05: Verify HashRefreshToken is never called and BeginTransactionAsync is never called.
            tokenService.Verify(x => x.HashRefreshToken(It.IsAny<string>()), Times.Never);
            unitOfWork.Verify(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-07: Use a transaction and a row-locking repository query for single-use token rotation.
        /// <summary>Ensures rotation runs under a row lock so a token can be used only once.</summary>
        [Fact]
        public async Task ExecuteAsync_WithActiveSession_ShouldUseLockedTransactionAndCommit()
        {
            // STEP-01: Create session through CreateSession.
            var session = CreateSession();
            // STEP-02: Mock IAuthenticationRepository, IUnitOfWork and 1 more; stub repository.GetSessionByRefreshTokenHashForUpdateAsync and unitOfWork.SaveChangesAsync.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);
            var transaction = new Mock<IUnitOfWorkTransaction>();
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(
                repository,
                new Mock<IRecommendationService>(),
                unitOfWork,
                transaction);

            // STEP-04: Run useCase.ExecuteAsync.
            await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-05: Verify BeginTransactionAsync is called once, GetSessionByRefreshTokenHashForUpdateAsync is called once and 2 more.
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
        /// <summary>Ensures a failed save never commits a half-rotated session.</summary>
        [Fact]
        public async Task ExecuteAsync_WhenSaveFails_ShouldNotCommitTransaction()
        {
            // STEP-01: Mock IAuthenticationRepository, IUnitOfWork and 1 more; stub repository.GetSessionByRefreshTokenHashForUpdateAsync and unitOfWork.SaveChangesAsync.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateSession());
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Persistence failed."));
            var transaction = new Mock<IUnitOfWorkTransaction>();
            // STEP-02: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(
                repository,
                new Mock<IRecommendationService>(),
                unitOfWork,
                transaction);

            // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-04: Assert act throws InvalidOperationException.
            await act.Should().ThrowAsync<InvalidOperationException>();
            // STEP-05: Verify CommitAsync is never called.
            transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-09: Two concurrent refreshes with the same token (two frontend instances) both succeed.
        /// <summary>The locked first request rotates; the second, inside the grace period, gets a parallel session.</summary>
        [Fact]
        public async Task ExecuteAsync_WithConcurrentReuse_ShouldGiveBothRequestsAValidSession()
        {
            // STEP-01: Create originalSession through CreateSession.
            var originalSession = CreateSession();
            // STEP-02: Collect added sessions in addedSessions; mock IAuthenticationRepository and IUnitOfWork; stub GetSessionByRefreshTokenHashForUpdateAsync, AddSession and SaveChangesAsync.
            var addedSessions = new List<UserSession>();
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(originalSession);
            repository.Setup(x => x.AddSession(It.IsAny<UserSession>()))
                .Callback<UserSession>(addedSessions.Add);
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, new Mock<IRecommendationService>(), unitOfWork);
            // STEP-04: Create transactionGate as SemaphoreSlim.
            var transactionGate = new SemaphoreSlim(1, 1);
            // STEP-05: Stub unitOfWork.BeginTransactionAsync.
            unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
                .Returns<CancellationToken>(async ct =>
                {
                    await transactionGate.WaitAsync(ct);
                    return new SerializingTransaction(transactionGate);
                });

            // STEP-06: Run Task.WhenAll and keep attempts.
            var attempts = await Task.WhenAll(ExecuteAsync(), ExecuteAsync());

            // STEP-07: Assert both attempts succeed, two sessions were added, originalSession links to the first and none was revoked as theft.
            attempts.Should().OnlyContain(attempt => attempt.Result != null);
            addedSessions.Should().HaveCount(2);
            // The first successor stays valid; nothing was revoked as theft.
            originalSession.ReplacedBySession.Should().BeSameAs(addedSessions[0]);
            addedSessions.Should().OnlyContain(session => session.RevokedAt == null);
            repository.Verify(x => x.GetActiveSessionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

            // STEP-08: Define helper ExecuteAsync.
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

        // TEST-10: A token rotated less than 30 seconds ago gets a parallel session, not a theft response.
        /// <summary>Issues a new pair and leaves the first successor active when the duplicate arrives inside the grace period.</summary>
        [Fact]
        public async Task ExecuteAsync_WithTokenRotatedWithinGracePeriod_ShouldIssueParallelSession()
        {
            // STEP-01: Create successor and session, revoked 10 seconds ago and linked to successor.
            var successor = CreateSession();
            successor.Id = 2;
            successor.RefreshTokenHash = "successor-hash";
            var session = CreateSession();
            session.RevokedAt = Now.AddSeconds(-10);
            session.ReplacedBySession = successor;
            session.ReplacedBySessionId = successor.Id;
            // STEP-02: Declare addedSession to capture a call argument; mock IAuthenticationRepository and IUnitOfWork; stub the session lookup, AddSession and SaveChangesAsync.
            UserSession? addedSession = null;
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
            repository.Setup(x => x.AddSession(It.IsAny<UserSession>()))
                .Callback<UserSession>(value => addedSession = value);
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .Callback(() => addedSession!.Id = 79)
                .ReturnsAsync(1);
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, new Mock<IRecommendationService>(), unitOfWork);

            // STEP-04: Run useCase.ExecuteAsync and keep result.
            var result = await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-05: Assert result carries the new tokens with the new session id, successor stays active and session is unchanged.
            result.RefreshToken.Should().Be("new-token");
            result.AccessToken.Should().Be("new-access-token-sid-79");
            successor.RevokedAt.Should().BeNull();
            session.ReplacedBySession.Should().BeSameAs(successor);
            session.RevokedAt.Should().Be(Now.AddSeconds(-10));
            // STEP-06: Verify GetActiveSessionsAsync is never called.
            repository.Verify(x => x.GetActiveSessionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-11: Past the grace period, a rotated token is reuse again and ends every session.
        /// <summary>Treats a duplicate that arrives after 30 seconds as theft.</summary>
        [Fact]
        public async Task ExecuteAsync_WithTokenRotatedBeforeGracePeriod_ShouldRevokeActiveSessions()
        {
            // STEP-01: Create successor and session, revoked exactly ReuseGracePeriod ago and linked to successor.
            var successor = CreateSession();
            successor.Id = 2;
            var session = CreateSession();
            session.RevokedAt = Now - RefreshTokenUseCase.ReuseGracePeriod;
            session.ReplacedBySession = successor;
            session.ReplacedBySessionId = successor.Id;
            // STEP-02: Mock IAuthenticationRepository and IUnitOfWork; stub the session lookup, GetActiveSessionsAsync and SaveChangesAsync.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
            repository.Setup(x => x.GetActiveSessionsAsync(12, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<UserSession> { successor });
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, new Mock<IRecommendationService>(), unitOfWork);

            // STEP-04: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-05: Assert act throws UnauthorizedException with "REFRESH_TOKEN_INVALID" and successor.RevokedAt is Now.
            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("REFRESH_TOKEN_INVALID");
            successor.RevokedAt.Should().Be(Now);
            // STEP-06: Verify AddSession is never called.
            repository.Verify(x => x.AddSession(It.IsAny<UserSession>()), Times.Never);
        }

        // TEST-12: Rotated and then logged out within the grace period: refused, but not treated as theft.
        /// <summary>A logout of the successor cannot be undone by replaying the previous token.</summary>
        [Fact]
        public async Task ExecuteAsync_WithSuccessorLoggedOutWithinGracePeriod_ShouldRejectWithoutRevokingAll()
        {
            // STEP-01: Create successor logged out 2 seconds ago and session rotated 5 seconds ago, linked to successor.
            var successor = CreateSession();
            successor.Id = 2;
            successor.RevokedAt = Now.AddSeconds(-2);
            var session = CreateSession();
            session.RevokedAt = Now.AddSeconds(-5);
            session.ReplacedBySession = successor;
            session.ReplacedBySessionId = successor.Id;
            // STEP-02: Mock IAuthenticationRepository and IUnitOfWork; stub repository.GetSessionByRefreshTokenHashForUpdateAsync.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetSessionByRefreshTokenHashForUpdateAsync("old-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
            var unitOfWork = new Mock<IUnitOfWork>();
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, new Mock<IRecommendationService>(), unitOfWork);

            // STEP-04: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-05: Assert act throws UnauthorizedException and exception.ErrorCode is "REFRESH_TOKEN_INVALID".
            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("REFRESH_TOKEN_INVALID");
            // STEP-06: Verify AddSession, GetActiveSessionsAsync and SaveChangesAsync are never called.
            repository.Verify(x => x.AddSession(It.IsAny<UserSession>()), Times.Never);
            repository.Verify(x => x.GetActiveSessionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        /// <summary>Creates the use case from the given mocks, a fixed clock and an optional transaction double.</summary>
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
            tokenService.Setup(x => x.CreateRefreshToken())
                .Returns(new GeneratedRefreshToken("new-token", "new-hash", Now.AddDays(30)));
            tokenService.Setup(x => x.CreateAccessToken(It.IsAny<User>(), It.IsAny<long>()))
                .Returns<User, long>((_, sessionId) => new IssuedAccessToken($"new-access-token-sid-{sessionId}", Now.AddMinutes(15)));

            return new RefreshTokenUseCase(
                new RefreshTokenRequestDtoValidator(),
                repository.Object,
                tokenService.Object,
                recommendationService.Object,
                unitOfWork.Object,
                new FixedTimeProvider(Now));
        }

        /// <summary>Creates a refresh request carrying the default old token.</summary>
        private static RefreshTokenRequestDto ValidRequest()
        {
            return new RefreshTokenRequestDto { RefreshToken = "old-token" };
        }

        /// <summary>Creates an active, unexpired session for user 12 that matches the default token hash.</summary>
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

        /// <summary>Clock fixed at one instant so expiry and revocation boundaries are deterministic.</summary>
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

        /// <summary>Transaction double that holds a shared gate until commit or dispose, simulating a row lock between concurrent refreshes.</summary>
        private sealed class SerializingTransaction : IUnitOfWorkTransaction
        {
            private readonly SemaphoreSlim _gate;
            private bool _released;

            public SerializingTransaction(SemaphoreSlim gate)
            {
                _gate = gate;
            }

            /// <summary>Releases the simulated row lock when the transaction commits.</summary>
            public Task CommitAsync(CancellationToken ct)
            {
                Release();
                return Task.CompletedTask;
            }

            /// <summary>Releases the simulated row lock when the transaction is disposed without commit.</summary>
            public ValueTask DisposeAsync()
            {
                Release();
                return ValueTask.CompletedTask;
            }

            /// <summary>Releases the gate at most once, whichever of commit or dispose happens first.</summary>
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
