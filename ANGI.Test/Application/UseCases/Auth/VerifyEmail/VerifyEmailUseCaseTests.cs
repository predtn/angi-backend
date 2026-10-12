using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Auth.VerifyEmail;
using ANGI.Application.UseCases.Validators.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.Test.Application.UseCases.Auth.VerifyEmail
{
    public sealed class VerifyEmailUseCaseTests
    {
        private static readonly DateTime Now = new(2026, 10, 11, 3, 0, 0, DateTimeKind.Utc);

        // TEST-01: Consume a valid email verification token and activate its user.
        /// <summary>Verifies AUTH-02 persists the verification timestamp and active status atomically.</summary>
        [Fact]
        public async Task ExecuteAsync_WithValidToken_ShouldVerifyAndActivateUser()
        {
            var userToken = CreateToken();
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserTokenByHashForUpdateAsync("token-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(userToken);
            var unitOfWork = CreateUnitOfWork(out var transaction);
            var accountStatusCache = new Mock<IAccountStatusCache>();
            var useCase = CreateUseCase(repository, unitOfWork, accountStatusCache);

            var result = await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            userToken.UsedAt.Should().Be(Now);
            userToken.User.EmailVerifiedAt.Should().Be(Now);
            userToken.User.Status.Should().Be(UserStatus.Active);
            result.UserId.Should().Be(12);
            result.EmailVerifiedAt.Should().Be(Now);
            result.Status.Should().Be("active");
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
            transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
            accountStatusCache.Verify(x => x.Invalidate(userToken.UserId), Times.Once);
        }

        // TEST-02: Reject a token hash that is absent from the database.
        /// <summary>Verifies AUTH-02 returns TOKEN_INVALID without persisting unknown tokens.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUnknownToken_ShouldThrowTokenInvalid()
        {
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserTokenByHashForUpdateAsync("token-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync((UserToken?)null);
            var unitOfWork = CreateUnitOfWork(out _);
            var useCase = CreateUseCase(repository, unitOfWork);

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            var exception = await act.Should().ThrowAsync<BadRequestException>();
            exception.Which.ErrorCode.Should().Be("TOKEN_INVALID");
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-03: Reject an expired email verification token.
        /// <summary>Verifies AUTH-02 returns TOKEN_EXPIRED at the expiry boundary.</summary>
        [Fact]
        public async Task ExecuteAsync_WithExpiredToken_ShouldThrowTokenExpired()
        {
            var userToken = CreateToken();
            userToken.ExpiresAt = Now;
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserTokenByHashForUpdateAsync("token-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(userToken);
            var unitOfWork = CreateUnitOfWork(out _);
            var useCase = CreateUseCase(repository, unitOfWork);

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            var exception = await act.Should().ThrowAsync<BadRequestException>();
            exception.Which.ErrorCode.Should().Be("TOKEN_EXPIRED");
        }

        // TEST-04: Reject an email verification token that was already consumed.
        /// <summary>Verifies AUTH-02 returns TOKEN_ALREADY_USED before considering token expiry.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUsedToken_ShouldThrowTokenAlreadyUsed()
        {
            var userToken = CreateToken();
            userToken.UsedAt = Now.AddHours(-1);
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserTokenByHashForUpdateAsync("token-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(userToken);
            var unitOfWork = CreateUnitOfWork(out _);
            var useCase = CreateUseCase(repository, unitOfWork);

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            var exception = await act.Should().ThrowAsync<ConflictException>();
            exception.Which.ErrorCode.Should().Be("TOKEN_ALREADY_USED");
        }

        // TEST-05: Validate an empty verification token before opening a transaction.
        /// <summary>Verifies request validation is the first AUTH-02 operation.</summary>
        [Fact]
        public async Task ExecuteAsync_WithEmptyToken_ShouldValidateBeforeDatabaseAccess()
        {
            var repository = new Mock<IAuthenticationRepository>();
            var unitOfWork = new Mock<IUnitOfWork>();
            var useCase = CreateUseCase(repository, unitOfWork);

            var act = () => useCase.ExecuteAsync(new VerifyEmailRequestDto(), CancellationToken.None);

            await act.Should().ThrowAsync<ValidationException>();
            unitOfWork.Verify(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-06: Prevent an old verification token from reactivating every non-pending account state.
        /// <summary>Verifies AUTH-02 cannot overwrite active, suspended, banned, or deactivated state.</summary>
        [Theory]
        [InlineData(UserStatus.Active)]
        [InlineData(UserStatus.Suspended)]
        [InlineData(UserStatus.Banned)]
        [InlineData(UserStatus.Deactivated)]
        public async Task ExecuteAsync_WithNonPendingUser_ShouldThrowTokenInvalid(UserStatus status)
        {
            var userToken = CreateToken();
            userToken.User.Status = status;
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserTokenByHashForUpdateAsync("token-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(userToken);
            var unitOfWork = CreateUnitOfWork(out _);
            var useCase = CreateUseCase(repository, unitOfWork);

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            var exception = await act.Should().ThrowAsync<BadRequestException>();
            exception.Which.ErrorCode.Should().Be("TOKEN_INVALID");
            userToken.User.Status.Should().Be(status);
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-07: Reject a password-reset token at the email-verification endpoint.
        /// <summary>Verifies AUTH-02 checks token purpose before any state mutation.</summary>
        [Fact]
        public async Task ExecuteAsync_WithPasswordResetToken_ShouldThrowTokenInvalid()
        {
            var userToken = CreateToken();
            userToken.Purpose = UserTokenPurpose.PasswordReset;
            var repository = RepositoryReturning(userToken);
            var unitOfWork = CreateUnitOfWork(out _);
            var useCase = CreateUseCase(repository, unitOfWork);

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("TOKEN_INVALID");
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-08: Prefer TOKEN_ALREADY_USED when a token is both used and expired.
        /// <summary>Verifies AUTH-02 error precedence does not relabel a consumed token as expired.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUsedAndExpiredToken_ShouldThrowTokenAlreadyUsed()
        {
            var userToken = CreateToken();
            userToken.UsedAt = Now.AddHours(-2);
            userToken.ExpiresAt = Now.AddHours(-1);
            var repository = RepositoryReturning(userToken);
            var useCase = CreateUseCase(repository, CreateUnitOfWork(out _));

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            (await act.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("TOKEN_ALREADY_USED");
        }

        // TEST-09: Accept a token whose expiry is one tick in the future.
        /// <summary>Verifies the inclusive expiry check does not reject the last valid tick.</summary>
        [Fact]
        public async Task ExecuteAsync_WithOneTickRemaining_ShouldVerifyUser()
        {
            var userToken = CreateToken();
            userToken.ExpiresAt = Now.AddTicks(1);
            var repository = RepositoryReturning(userToken);
            var useCase = CreateUseCase(repository, CreateUnitOfWork(out _));

            var result = await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            result.Status.Should().Be("active");
            userToken.UsedAt.Should().Be(Now);
        }

        // TEST-10: Return the documented conflict when the exact persisted post-verification state is reused.
        /// <summary>Verifies a successfully consumed token remains TOKEN_ALREADY_USED after its user becomes active.</summary>
        [Fact]
        public async Task ExecuteAsync_WithPersistedPostVerificationState_ShouldThrowTokenAlreadyUsed()
        {
            var userToken = CreateToken();
            userToken.UsedAt = Now.AddMinutes(-1);
            userToken.User.Status = UserStatus.Active;
            userToken.User.EmailVerifiedAt = Now.AddMinutes(-1);
            var repository = RepositoryReturning(userToken);
            var unitOfWork = CreateUnitOfWork(out _);
            var useCase = CreateUseCase(repository, unitOfWork);

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            (await act.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("TOKEN_ALREADY_USED");
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        /// <summary>Creates the use case with deterministic token hashing and time.</summary>
        private static VerifyEmailUseCase CreateUseCase(
            Mock<IAuthenticationRepository> repository,
            Mock<IUnitOfWork> unitOfWork,
            Mock<IAccountStatusCache>? accountStatusCache = null)
        {
            var tokenService = new Mock<IAuthenticationTokenService>();
            tokenService.Setup(x => x.HashUserToken("raw-token")).Returns("token-hash");
            return new VerifyEmailUseCase(
                new VerifyEmailRequestDtoValidator(),
                repository.Object,
                tokenService.Object,
                (accountStatusCache ?? new Mock<IAccountStatusCache>()).Object,
                unitOfWork.Object,
                new FixedTimeProvider(Now));
        }

        /// <summary>Creates a unit of work whose transaction can be asserted by a test.</summary>
        private static Mock<IUnitOfWork> CreateUnitOfWork(out Mock<IUnitOfWorkTransaction> transaction)
        {
            transaction = new Mock<IUnitOfWorkTransaction>();
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(transaction.Object);
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);
            return unitOfWork;
        }

        /// <summary>Creates a repository mock that returns the supplied token under the expected hash.</summary>
        private static Mock<IAuthenticationRepository> RepositoryReturning(UserToken token)
        {
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserTokenByHashForUpdateAsync(
                    "token-hash",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(token);
            return repository;
        }

        /// <summary>Creates a valid pending verification token and its owning user.</summary>
        private static UserToken CreateToken()
        {
            return new UserToken
            {
                Id = 1,
                UserId = 12,
                Purpose = UserTokenPurpose.EmailVerify,
                TokenHash = "token-hash",
                ExpiresAt = Now.AddHours(1),
                User = new User
                {
                    Id = 12,
                    Email = "user@angi.test",
                    DisplayName = "Test User",
                    Status = UserStatus.PendingVerification
                }
            };
        }

        /// <summary>Creates a request containing the raw token represented by the repository hash.</summary>
        private static VerifyEmailRequestDto ValidRequest()
        {
            return new VerifyEmailRequestDto { Token = "raw-token" };
        }

        private sealed class FixedTimeProvider : TimeProvider
        {
            private readonly DateTimeOffset _now;

            /// <summary>Creates a clock fixed at the supplied UTC instant.</summary>
            public FixedTimeProvider(DateTime now)
            {
                _now = new DateTimeOffset(now);
            }

            /// <summary>Returns the deterministic UTC instant used by the test.</summary>
            public override DateTimeOffset GetUtcNow() => _now;
        }
    }
}
