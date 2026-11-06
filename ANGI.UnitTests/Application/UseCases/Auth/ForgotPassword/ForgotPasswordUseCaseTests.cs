using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Auth.ForgotPassword;
using ANGI.Application.UseCases.Validators.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Auth.ForgotPassword
{
    /// <summary>Verifies ForgotPasswordUseCase issues a single valid reset token and never reveals whether an email is registered.</summary>
    public sealed class ForgotPasswordUseCaseTests
    {
        private static readonly DateTime _now = new(2026, 10, 14, 3, 0, 0, DateTimeKind.Utc);

        // TEST-01: Invalidate old reset tokens, persist a replacement, and email its raw value.
        /// <summary>Verifies AUTH-08 leaves only the new 30-minute reset token usable.</summary>
        [Fact]
        public async Task ExecuteAsync_WithExistingUser_ShouldReplaceTokensAndSendEmail()
        {
            // STEP-01: Create user through CreateUser, create firstOldToken as UserToken and create secondOldToken as UserToken.
            var user = CreateUser();
            var firstOldToken = new UserToken { UserId = user.Id, Purpose = UserTokenPurpose.PasswordReset };
            var secondOldToken = new UserToken { UserId = user.Id, Purpose = UserTokenPurpose.PasswordReset };
            // STEP-02: Declare addedToken to capture a call argument; mock IAuthenticationRepository and 1 more; stub repository.GetUserByEmailForUpdateAsync and 2 more.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailForUpdateAsync("user@angi.test", It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            repository.Setup(x => x.GetUnusedUserTokensAsync(
                    user.Id,
                    UserTokenPurpose.PasswordReset,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([firstOldToken, secondOldToken]);
            UserToken? addedToken = null;
            repository.Setup(x => x.AddUserToken(It.IsAny<UserToken>()))
                .Callback<UserToken>(token => addedToken = token);
            var emailService = new Mock<IEmailService>();
            // STEP-03: Create unitOfWork through CreateUnitOfWork.
            var unitOfWork = CreateUnitOfWork();
            // STEP-04: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, emailService, unitOfWork);

            // STEP-05: Run useCase.ExecuteAsync with ForgotPasswordRequestDto { Email = " USER@ANGI.TEST " }.
            await useCase.ExecuteAsync(
                new ForgotPasswordRequestDto { Email = " USER@ANGI.TEST " },
                CancellationToken.None);

            // STEP-06: Assert firstOldToken.UsedAt is _now and secondOldToken.UsedAt is _now.
            firstOldToken.UsedAt.Should().Be(_now);
            secondOldToken.UsedAt.Should().Be(_now);
            // STEP-07: Assert addedToken is not null, addedToken.UserId is user.Id, addedToken.Purpose is UserTokenPurpose.PasswordReset and 1 more.
            addedToken.Should().NotBeNull();
            addedToken!.UserId.Should().Be(user.Id);
            addedToken.Purpose.Should().Be(UserTokenPurpose.PasswordReset);
            addedToken.TokenHash.Should().Be("new-hash");
            // STEP-08: Assert addedToken.ExpiresAt is _now.AddMinutes().
            addedToken.ExpiresAt.Should().Be(_now.AddMinutes(30));
            // STEP-09: Verify SaveChangesAsync is called once and SendPasswordResetAsync is called once.
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
            emailService.Verify(x => x.SendPasswordResetAsync(
                user.Email,
                user.DisplayName,
                "new-token",
                TimeSpan.FromMinutes(30),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-02: Return successfully for an unknown email without revealing account existence.
        /// <summary>Verifies AUTH-08 performs no write or delivery when no account matches.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUnknownEmail_ShouldReturnWithoutSideEffects()
        {
            // STEP-01: Mock IAuthenticationRepository and IEmailService; stub repository.GetUserByEmailForUpdateAsync.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailForUpdateAsync("missing@angi.test", It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);
            var emailService = new Mock<IEmailService>();
            // STEP-02: Create unitOfWork through CreateUnitOfWork.
            var unitOfWork = CreateUnitOfWork();
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, emailService, unitOfWork);

            // STEP-04: Run useCase.ExecuteAsync with ForgotPasswordRequestDto { Email = "missing@angi.test" }.
            await useCase.ExecuteAsync(
                new ForgotPasswordRequestDto { Email = "missing@angi.test" },
                CancellationToken.None);

            // STEP-05: Verify AddUserToken is never called, SaveChangesAsync is never called and SendPasswordResetAsync is never called.
            repository.Verify(x => x.AddUserToken(It.IsAny<UserToken>()), Times.Never);
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
            emailService.Verify(x => x.SendPasswordResetAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-03: Allow every existing account status, including Google-only accounts, to request a reset.
        /// <summary>Verifies AUTH-08 does not incorrectly gate password creation on password or account status.</summary>
        [Theory]
        [InlineData(UserStatus.PendingVerification)]
        [InlineData(UserStatus.Active)]
        [InlineData(UserStatus.Suspended)]
        [InlineData(UserStatus.Banned)]
        [InlineData(UserStatus.Deactivated)]
        public async Task ExecuteAsync_WithAnyExistingAccountStatus_ShouldSendResetEmail(UserStatus status)
        {
            // STEP-01: Create user through CreateUser, set user.Status to status and set user.PasswordHash to null.
            var user = CreateUser();
            user.Status = status;
            user.PasswordHash = null;
            // STEP-02: Mock IAuthenticationRepository and IEmailService; stub repository.GetUserByEmailForUpdateAsync and repository.GetUnusedUserTokensAsync.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailForUpdateAsync(user.Email, It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            repository.Setup(x => x.GetUnusedUserTokensAsync(
                    user.Id,
                    UserTokenPurpose.PasswordReset,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var emailService = new Mock<IEmailService>();
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, emailService, CreateUnitOfWork());

            // STEP-04: Run useCase.ExecuteAsync with ForgotPasswordRequestDto { Email = user.Email }.
            await useCase.ExecuteAsync(
                new ForgotPasswordRequestDto { Email = user.Email },
                CancellationToken.None);

            // STEP-05: Verify SendPasswordResetAsync is called once.
            emailService.Verify(x => x.SendPasswordResetAsync(
                user.Email,
                user.DisplayName,
                "new-token",
                TimeSpan.FromMinutes(30),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-04: Reject a malformed email before querying account data.
        /// <summary>Verifies AUTH-08 validation is the first operation in the workflow.</summary>
        [Fact]
        public async Task ExecuteAsync_WithMalformedEmail_ShouldValidateBeforeRepositoryAccess()
        {
            // STEP-01: Mock IAuthenticationRepository.
            var repository = new Mock<IAuthenticationRepository>();
            // STEP-02: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(
                repository,
                new Mock<IEmailService>(),
                CreateUnitOfWork());

            // STEP-03: Wrap useCase.ExecuteAsync with ForgotPasswordRequestDto { Email = "not-an-email" } in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(
                new ForgotPasswordRequestDto { Email = "not-an-email" },
                CancellationToken.None);

            // STEP-04: Assert act throws ValidationException.
            await act.Should().ThrowAsync<ValidationException>();
            // STEP-05: Verify GetUserByEmailForUpdateAsync is never called.
            repository.Verify(x => x.GetUserByEmailForUpdateAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-05: Surface email-provider failure only after the token transaction is committed.
        /// <summary>Verifies a durable reset token exists before an AUTH-08 delivery failure is reported.</summary>
        [Fact]
        public async Task ExecuteAsync_WhenEmailDeliveryFails_ShouldPreserveCommittedToken()
        {
            // STEP-01: Create user through CreateUser.
            var user = CreateUser();
            // STEP-02: Mock IAuthenticationRepository, IEmailService and 2 more; stub repository.GetUserByEmailForUpdateAsync, repository.GetUnusedUserTokensAsync and 3 more.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailForUpdateAsync(user.Email, It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            repository.Setup(x => x.GetUnusedUserTokensAsync(
                    user.Id,
                    UserTokenPurpose.PasswordReset,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var emailService = new Mock<IEmailService>();
            emailService.Setup(x => x.SendPasswordResetAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ServiceUnavailableException("SERVICE_UNAVAILABLE", "Không gửi được email."));
            var transaction = new Mock<IUnitOfWorkTransaction>();
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(transaction.Object);
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, emailService, unitOfWork);

            // STEP-04: Wrap useCase.ExecuteAsync with ForgotPasswordRequestDto { Email = user.Email } in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(
                new ForgotPasswordRequestDto { Email = user.Email },
                CancellationToken.None);

            // STEP-05: Assert act throws ServiceUnavailableException.
            await act.Should().ThrowAsync<ServiceUnavailableException>();
            // STEP-06: Verify SaveChangesAsync is called once and CommitAsync is called once.
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
            transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        /// <summary>Creates a deterministic unit of work with a successful transaction and save.</summary>
        private static Mock<IUnitOfWork> CreateUnitOfWork()
        {
            var transaction = new Mock<IUnitOfWorkTransaction>();
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(transaction.Object);
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            return unitOfWork;
        }

        /// <summary>Creates AUTH-08 with deterministic token generation and time.</summary>
        private static ForgotPasswordUseCase CreateUseCase(
            Mock<IAuthenticationRepository> repository,
            Mock<IEmailService> emailService,
            Mock<IUnitOfWork> unitOfWork)
        {
            var tokenService = new Mock<IAuthenticationTokenService>();
            tokenService.Setup(x => x.CreateUserToken())
                .Returns(new GeneratedUserToken("new-token", "new-hash"));
            return new ForgotPasswordUseCase(
                new ForgotPasswordRequestDtoValidator(),
                repository.Object,
                tokenService.Object,
                emailService.Object,
                unitOfWork.Object,
                new FixedTimeProvider(_now));
        }

        /// <summary>Creates an account eligible for a password-reset request.</summary>
        private static User CreateUser() => new()
        {
            Id = 12,
            Email = "user@angi.test",
            DisplayName = "Test User",
            Status = UserStatus.Active
        };

        /// <summary>Clock fixed at one instant so token expiry can be asserted exactly.</summary>
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
