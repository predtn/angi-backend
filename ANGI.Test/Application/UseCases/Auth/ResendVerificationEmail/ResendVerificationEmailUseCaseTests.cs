using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Auth.ResendVerificationEmail;
using ANGI.Application.UseCases.Validators.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.Test.Application.UseCases.Auth.ResendVerificationEmail
{
    public sealed class ResendVerificationEmailUseCaseTests
    {
        private static readonly DateTime Now = new(2026, 10, 11, 3, 0, 0, DateTimeKind.Utc);

        // TEST-01: Invalidate old tokens, persist a replacement, and email its raw value.
        /// <summary>Verifies AUTH-03 replaces outstanding tokens before sending a 24-hour verification link.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUnverifiedUser_ShouldReplaceTokenAndSendEmail()
        {
            var user = CreateUnverifiedUser();
            var oldToken = new UserToken { UserId = user.Id, Purpose = UserTokenPurpose.EmailVerify };
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailForUpdateAsync("user@angi.test", It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            repository.Setup(x => x.GetUnusedUserTokensAsync(
                    user.Id,
                    UserTokenPurpose.EmailVerify,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<UserToken> { oldToken });
            UserToken? addedToken = null;
            repository.Setup(x => x.AddUserToken(It.IsAny<UserToken>()))
                .Callback<UserToken>(token => addedToken = token);
            var emailService = new Mock<IEmailService>();
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);
            var useCase = CreateUseCase(repository, emailService, unitOfWork);

            await useCase.ExecuteAsync(
                new ResendVerificationEmailRequestDto { Email = " USER@ANGI.TEST " },
                CancellationToken.None);

            oldToken.UsedAt.Should().Be(Now);
            addedToken.Should().NotBeNull();
            addedToken!.UserId.Should().Be(user.Id);
            addedToken.Purpose.Should().Be(UserTokenPurpose.EmailVerify);
            addedToken.TokenHash.Should().Be("new-hash");
            addedToken.ExpiresAt.Should().Be(Now.AddHours(24));
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
            emailService.Verify(x => x.SendEmailVerificationAsync(
                user.Email,
                user.DisplayName,
                "new-token",
                TimeSpan.FromHours(24),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-02: Return successfully for an unknown email without revealing account existence.
        /// <summary>Verifies AUTH-03 performs no writes or email delivery for an unknown address.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUnknownEmail_ShouldReturnWithoutSideEffects()
        {
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailForUpdateAsync("missing@angi.test", It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);
            var emailService = new Mock<IEmailService>();
            var unitOfWork = new Mock<IUnitOfWork>();
            var useCase = CreateUseCase(repository, emailService, unitOfWork);

            await useCase.ExecuteAsync(
                new ResendVerificationEmailRequestDto { Email = "missing@angi.test" },
                CancellationToken.None);

            repository.Verify(x => x.AddUserToken(It.IsAny<UserToken>()), Times.Never);
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
            emailService.Verify(x => x.SendEmailVerificationAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-03: Return successfully for an account whose email is already verified.
        /// <summary>Verifies AUTH-03 preserves its non-enumerating response for verified accounts.</summary>
        [Fact]
        public async Task ExecuteAsync_WithVerifiedUser_ShouldReturnWithoutSendingEmail()
        {
            var user = CreateUnverifiedUser();
            user.EmailVerifiedAt = Now.AddDays(-1);
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailForUpdateAsync("user@angi.test", It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            var emailService = new Mock<IEmailService>();
            var unitOfWork = new Mock<IUnitOfWork>();
            var useCase = CreateUseCase(repository, emailService, unitOfWork);

            await useCase.ExecuteAsync(
                new ResendVerificationEmailRequestDto { Email = "user@angi.test" },
                CancellationToken.None);

            emailService.Verify(x => x.SendEmailVerificationAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-04: Reject a malformed email before querying account data.
        /// <summary>Verifies AUTH-03 validation runs before repository access.</summary>
        [Fact]
        public async Task ExecuteAsync_WithMalformedEmail_ShouldValidateBeforeRepositoryAccess()
        {
            var repository = new Mock<IAuthenticationRepository>();
            var useCase = CreateUseCase(
                repository,
                new Mock<IEmailService>(),
                new Mock<IUnitOfWork>());

            var act = () => useCase.ExecuteAsync(
                new ResendVerificationEmailRequestDto { Email = "not-an-email" },
                CancellationToken.None);

            await act.Should().ThrowAsync<ValidationException>();
            repository.Verify(x => x.GetUserByEmailForUpdateAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        /// <summary>Creates the resend use case with deterministic token generation and time.</summary>
        private static ResendVerificationEmailUseCase CreateUseCase(
            Mock<IAuthenticationRepository> repository,
            Mock<IEmailService> emailService,
            Mock<IUnitOfWork> unitOfWork)
        {
            var tokenService = new Mock<IAuthenticationTokenService>();
            tokenService.Setup(x => x.CreateUserToken())
                .Returns(new GeneratedUserToken("new-token", "new-hash"));
            var transaction = new Mock<IUnitOfWorkTransaction>();
            unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(transaction.Object);
            return new ResendVerificationEmailUseCase(
                new ResendVerificationEmailRequestDtoValidator(),
                repository.Object,
                tokenService.Object,
                emailService.Object,
                unitOfWork.Object,
                new FixedTimeProvider(Now));
        }

        /// <summary>Creates an account eligible to receive a verification email.</summary>
        private static User CreateUnverifiedUser()
        {
            return new User
            {
                Id = 12,
                Email = "user@angi.test",
                DisplayName = "Test User",
                Status = UserStatus.PendingVerification
            };
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
