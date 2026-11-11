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

namespace ANGI.UnitTests.Application.UseCases.Auth.VerifyEmail
{
    /// <summary>Verifies VerifyEmailUseCase activates an account only for a valid, unused, unexpired verification token.</summary>
    public sealed class VerifyEmailUseCaseTests
    {
        private static readonly DateTime Now = new(2026, 10, 11, 3, 0, 0, DateTimeKind.Utc);

        // TEST-01: Consume a valid email verification token and activate its user.
        /// <summary>Verifies AUTH-02 persists the verification timestamp and active status atomically.</summary>
        [Fact]
        public async Task ExecuteAsync_WithValidToken_ShouldVerifyAndActivateUser()
        {
            // STEP-01: Create userToken through CreateToken.
            var userToken = CreateToken();
            // STEP-02: Create unitOfWork through CreateUnitOfWork; mock IAuthenticationRepository and IAccountStatusCache; stub repository.GetUserTokenByHashForUpdateAsync.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserTokenByHashForUpdateAsync("token-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(userToken);
            var unitOfWork = CreateUnitOfWork(out var transaction);
            var accountStatusCache = new Mock<IAccountStatusCache>();
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, unitOfWork, accountStatusCache);

            // STEP-04: Run useCase.ExecuteAsync and keep result.
            var result = await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-05: Assert userToken.UsedAt is Now, userToken.User.EmailVerifiedAt is Now and userToken.User.Status is UserStatus.Active.
            userToken.UsedAt.Should().Be(Now);
            userToken.User.EmailVerifiedAt.Should().Be(Now);
            userToken.User.Status.Should().Be(UserStatus.Active);
            // STEP-06: Assert result.UserId is 12, result.EmailVerifiedAt is Now and result.Status is "active".
            result.UserId.Should().Be(12);
            result.EmailVerifiedAt.Should().Be(Now);
            result.Status.Should().Be("active");
            // STEP-07: Verify SaveChangesAsync is called once and CommitAsync is called once.
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
            transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
            // STEP-08: Verify Invalidate is called once.
            accountStatusCache.Verify(x => x.Invalidate(userToken.UserId), Times.Once);
        }

        // TEST-02: Reject a token hash that is absent from the database.
        /// <summary>Verifies AUTH-02 returns TOKEN_INVALID without persisting unknown tokens.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUnknownToken_ShouldThrowTokenInvalid()
        {
            // STEP-01: Mock IAuthenticationRepository; stub repository.GetUserTokenByHashForUpdateAsync.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserTokenByHashForUpdateAsync("token-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync((UserToken?)null);
            // STEP-02: Create unitOfWork through CreateUnitOfWork.
            var unitOfWork = CreateUnitOfWork(out _);
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, unitOfWork);

            // STEP-04: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-05: Assert act throws BadRequestException and exception.ErrorCode is "TOKEN_INVALID".
            var exception = await act.Should().ThrowAsync<BadRequestException>();
            exception.Which.ErrorCode.Should().Be("TOKEN_INVALID");
            // STEP-06: Verify SaveChangesAsync is never called.
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-03: Reject an expired email verification token.
        /// <summary>Verifies AUTH-02 returns TOKEN_EXPIRED at the expiry boundary.</summary>
        [Fact]
        public async Task ExecuteAsync_WithExpiredToken_ShouldThrowTokenExpired()
        {
            // STEP-01: Create userToken through CreateToken and set userToken.ExpiresAt to Now.
            var userToken = CreateToken();
            userToken.ExpiresAt = Now;
            // STEP-02: Mock IAuthenticationRepository; stub repository.GetUserTokenByHashForUpdateAsync.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserTokenByHashForUpdateAsync("token-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(userToken);
            // STEP-03: Create unitOfWork through CreateUnitOfWork.
            var unitOfWork = CreateUnitOfWork(out _);
            // STEP-04: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, unitOfWork);

            // STEP-05: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-06: Assert act throws BadRequestException and exception.ErrorCode is "TOKEN_EXPIRED".
            var exception = await act.Should().ThrowAsync<BadRequestException>();
            exception.Which.ErrorCode.Should().Be("TOKEN_EXPIRED");
        }

        // TEST-04: Reject an email verification token that was already consumed.
        /// <summary>Verifies AUTH-02 returns TOKEN_ALREADY_USED before considering token expiry.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUsedToken_ShouldThrowTokenAlreadyUsed()
        {
            // STEP-01: Create userToken through CreateToken and set userToken.UsedAt to Now.AddHours().
            var userToken = CreateToken();
            userToken.UsedAt = Now.AddHours(-1);
            // STEP-02: Mock IAuthenticationRepository; stub repository.GetUserTokenByHashForUpdateAsync.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserTokenByHashForUpdateAsync("token-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(userToken);
            // STEP-03: Create unitOfWork through CreateUnitOfWork.
            var unitOfWork = CreateUnitOfWork(out _);
            // STEP-04: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, unitOfWork);

            // STEP-05: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-06: Assert act throws ConflictException and exception.ErrorCode is "TOKEN_ALREADY_USED".
            var exception = await act.Should().ThrowAsync<ConflictException>();
            exception.Which.ErrorCode.Should().Be("TOKEN_ALREADY_USED");
        }

        // TEST-05: Validate an empty verification token before opening a transaction.
        /// <summary>Verifies request validation is the first AUTH-02 operation.</summary>
        [Fact]
        public async Task ExecuteAsync_WithEmptyToken_ShouldValidateBeforeDatabaseAccess()
        {
            // STEP-01: Mock IAuthenticationRepository and IUnitOfWork.
            var repository = new Mock<IAuthenticationRepository>();
            var unitOfWork = new Mock<IUnitOfWork>();
            // STEP-02: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, unitOfWork);

            // STEP-03: Wrap useCase.ExecuteAsync with an empty VerifyEmailRequestDto in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(new VerifyEmailRequestDto(), CancellationToken.None);

            // STEP-04: Assert act throws ValidationException.
            await act.Should().ThrowAsync<ValidationException>();
            // STEP-05: Verify BeginTransactionAsync is never called.
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
            // STEP-01: Create userToken through CreateToken and set userToken.User.Status to status.
            var userToken = CreateToken();
            userToken.User.Status = status;
            // STEP-02: Mock IAuthenticationRepository; stub repository.GetUserTokenByHashForUpdateAsync.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserTokenByHashForUpdateAsync("token-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(userToken);
            // STEP-03: Create unitOfWork through CreateUnitOfWork.
            var unitOfWork = CreateUnitOfWork(out _);
            // STEP-04: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, unitOfWork);

            // STEP-05: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-06: Assert act throws BadRequestException, exception.ErrorCode is "TOKEN_INVALID" and userToken.User.Status is status.
            var exception = await act.Should().ThrowAsync<BadRequestException>();
            exception.Which.ErrorCode.Should().Be("TOKEN_INVALID");
            userToken.User.Status.Should().Be(status);
            // STEP-07: Verify SaveChangesAsync is never called.
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-07: Reject a password-reset token at the email-verification endpoint.
        /// <summary>Verifies AUTH-02 checks token purpose before any state mutation.</summary>
        [Fact]
        public async Task ExecuteAsync_WithPasswordResetToken_ShouldThrowTokenInvalid()
        {
            // STEP-01: Create userToken through CreateToken, set userToken.Purpose to UserTokenPurpose.PasswordReset, get repository from RepositoryReturning and 1 more.
            var userToken = CreateToken();
            userToken.Purpose = UserTokenPurpose.PasswordReset;
            var repository = RepositoryReturning(userToken);
            var unitOfWork = CreateUnitOfWork(out _);
            // STEP-02: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, unitOfWork);

            // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-04: Assert act throws BadRequestException and the thrown exception.ErrorCode is "TOKEN_INVALID".
            (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("TOKEN_INVALID");
            // STEP-05: Verify SaveChangesAsync is never called.
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-08: Prefer TOKEN_ALREADY_USED when a token is both used and expired.
        /// <summary>Verifies AUTH-02 error precedence does not relabel a consumed token as expired.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUsedAndExpiredToken_ShouldThrowTokenAlreadyUsed()
        {
            // STEP-01: Create userToken through CreateToken, set userToken.UsedAt to Now.AddHours(), set userToken.ExpiresAt to Now.AddHours() and 1 more.
            var userToken = CreateToken();
            userToken.UsedAt = Now.AddHours(-2);
            userToken.ExpiresAt = Now.AddHours(-1);
            var repository = RepositoryReturning(userToken);
            // STEP-02: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, CreateUnitOfWork(out _));

            // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-04: Assert act throws ConflictException and the thrown exception.ErrorCode is "TOKEN_ALREADY_USED".
            (await act.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("TOKEN_ALREADY_USED");
        }

        // TEST-09: Accept a token whose expiry is one tick in the future.
        /// <summary>Verifies the inclusive expiry check does not reject the last valid tick.</summary>
        [Fact]
        public async Task ExecuteAsync_WithOneTickRemaining_ShouldVerifyUser()
        {
            // STEP-01: Create userToken through CreateToken, set userToken.ExpiresAt to Now.AddTicks() and get repository from RepositoryReturning.
            var userToken = CreateToken();
            userToken.ExpiresAt = Now.AddTicks(1);
            var repository = RepositoryReturning(userToken);
            // STEP-02: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, CreateUnitOfWork(out _));

            // STEP-03: Run useCase.ExecuteAsync and keep result.
            var result = await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-04: Assert result.Status is "active" and userToken.UsedAt is Now.
            result.Status.Should().Be("active");
            userToken.UsedAt.Should().Be(Now);
        }

        // TEST-10: Return the documented conflict when the exact persisted post-verification state is reused.
        /// <summary>Verifies a successfully consumed token remains TOKEN_ALREADY_USED after its user becomes active.</summary>
        [Fact]
        public async Task ExecuteAsync_WithPersistedPostVerificationState_ShouldThrowTokenAlreadyUsed()
        {
            // STEP-01: Create userToken through CreateToken, set userToken.UsedAt to Now.AddMinutes(), set userToken.User.Status to UserStatus.Active and 3 more.
            var userToken = CreateToken();
            userToken.UsedAt = Now.AddMinutes(-1);
            userToken.User.Status = UserStatus.Active;
            userToken.User.EmailVerifiedAt = Now.AddMinutes(-1);
            var repository = RepositoryReturning(userToken);
            var unitOfWork = CreateUnitOfWork(out _);
            // STEP-02: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, unitOfWork);

            // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-04: Assert act throws ConflictException and the thrown exception.ErrorCode is "TOKEN_ALREADY_USED".
            (await act.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("TOKEN_ALREADY_USED");
            // STEP-05: Verify SaveChangesAsync is never called.
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

        /// <summary>Clock fixed at one instant so the token expiry boundary can be tested exactly.</summary>
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
