using System.Net;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.Common.Models.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Auth.Login;
using ANGI.Application.UseCases.Validators.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.Test.Application.UseCases.Auth.Login
{
    public sealed class LoginUseCaseTests
    {
        private static readonly DateTime Now = new(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);

        // TEST-01: Create a session and return the documented authentication result for valid credentials.
        [Fact]
        public async Task ExecuteAsync_WithValidCredentials_ShouldCreateSessionAndReturnAuthResult()
        {
            var user = CreateUser();
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailAsync("user@angi.test", It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            UserSession? addedSession = null;
            repository.Setup(x => x.AddSession(It.IsAny<UserSession>()))
                .Callback<UserSession>(session => addedSession = session);
            var auditLogService = new Mock<IAuditLogService>();
            AuditLogEntry? addedAuditLog = null;
            auditLogService.Setup(x => x.Add(It.IsAny<AuditLogEntry>()))
                .Callback<AuditLogEntry>(entry => addedAuditLog = entry);

            var passwordService = new Mock<IPasswordService>();
            passwordService.Setup(x => x.Verify("correct-password", "password-hash")).Returns(true);
            var recommendationService = new Mock<IRecommendationService>();
            recommendationService.Setup(x => x.GetSurveyCompletionAsync(user.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);

            var useCase = CreateUseCase(
                repository,
                passwordService,
                recommendationService,
                unitOfWork,
                auditLogService);

            var result = await useCase.ExecuteAsync(new LoginRequestDto
            {
                Email = "  User@ANGI.Test ",
                Password = "correct-password"
            }, CancellationToken.None);

            result.AccessToken.Should().Be("access-token");
            result.RefreshToken.Should().Be("refresh-token");
            result.User.Id.Should().Be(user.Id);
            result.User.Role.Should().Be("TRAVELER");
            result.User.Status.Should().Be("active");
            result.User.NeedsPreferenceSurvey.Should().BeTrue();
            user.LastLoginAt.Should().Be(Now);
            addedSession.Should().NotBeNull();
            addedSession!.RefreshTokenHash.Should().Be("refresh-hash");
            addedSession.IpAddress.Should().Be(IPAddress.Loopback);
            addedAuditLog.Should().NotBeNull();
            addedAuditLog!.Action.Should().Be("USER_LOGIN");
            addedAuditLog.EntityType.Should().Be("user");
            addedAuditLog.EntityId.Should().Be(user.Id);
            addedAuditLog.SubjectUserId.Should().Be(user.Id);
            addedAuditLog.ActorId.Should().Be(user.Id);
            addedAuditLog.ActorRole.Should().Be("TRAVELER");
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-02: Reject an incorrect password without creating a session or saving changes.
        [Fact]
        public async Task ExecuteAsync_WithIncorrectPassword_ShouldThrowInvalidCredentials()
        {
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateUser());
            var passwordService = new Mock<IPasswordService>();
            passwordService.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
            var unitOfWork = new Mock<IUnitOfWork>();
            var useCase = CreateUseCase(
                repository,
                passwordService,
                new Mock<IRecommendationService>(),
                unitOfWork);

            var act = () => useCase.ExecuteAsync(new LoginRequestDto
            {
                Email = "user@angi.test",
                Password = "wrong-password"
            }, CancellationToken.None);

            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("INVALID_CREDENTIALS");
            repository.Verify(x => x.AddSession(It.IsAny<UserSession>()), Times.Never);
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-03: Reject a suspended account with the endpoint-specific error code.
        [Fact]
        public async Task ExecuteAsync_WithSuspendedAccount_ShouldThrowAccountSuspended()
        {
            var user = CreateUser();
            user.Status = UserStatus.Suspended;
            user.SuspendedUntil = Now.AddDays(1);
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            var passwordService = new Mock<IPasswordService>();
            passwordService.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            var useCase = CreateUseCase(
                repository,
                passwordService,
                new Mock<IRecommendationService>(),
                new Mock<IUnitOfWork>());

            var act = () => useCase.ExecuteAsync(new LoginRequestDto
            {
                Email = "user@angi.test",
                Password = "correct-password"
            }, CancellationToken.None);

            var exception = await act.Should().ThrowAsync<AccountSuspendedException>();
            exception.Which.ErrorCode.Should().Be("ACCOUNT_SUSPENDED");
            exception.Which.SuspendedUntil.Should().Be(user.SuspendedUntil);
        }

        // TEST-04: Reject an unknown email without verifying a password or persisting login data.
        [Fact]
        public async Task ExecuteAsync_WithUnknownEmail_ShouldThrowInvalidCredentials()
        {
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);
            var passwordService = new Mock<IPasswordService>();
            var unitOfWork = new Mock<IUnitOfWork>();
            var useCase = CreateUseCase(repository, passwordService, new Mock<IRecommendationService>(), unitOfWork);

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("INVALID_CREDENTIALS");
            passwordService.Verify(x => x.Verify(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-05: Reject a password-only login for an account that has no password hash.
        [Fact]
        public async Task ExecuteAsync_WithoutPasswordHash_ShouldThrowInvalidCredentials()
        {
            var user = CreateUser();
            user.PasswordHash = null;
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            var passwordService = new Mock<IPasswordService>();
            var useCase = CreateUseCase(
                repository,
                passwordService,
                new Mock<IRecommendationService>(),
                new Mock<IUnitOfWork>());

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("INVALID_CREDENTIALS");
            passwordService.Verify(x => x.Verify(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        // TEST-06: Map every non-active account status to its documented login error code.
        [Theory]
        [InlineData(UserStatus.PendingVerification, "EMAIL_NOT_VERIFIED")]
        [InlineData(UserStatus.Banned, "ACCOUNT_BANNED")]
        [InlineData(UserStatus.Deactivated, "ACCOUNT_DEACTIVATED")]
        public async Task ExecuteAsync_WithBlockedAccountStatus_ShouldThrowDocumentedError(
            UserStatus status,
            string expectedErrorCode)
        {
            var user = CreateUser();
            user.Status = status;
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            var passwordService = new Mock<IPasswordService>();
            passwordService.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            var useCase = CreateUseCase(
                repository,
                passwordService,
                new Mock<IRecommendationService>(),
                new Mock<IUnitOfWork>());

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            var exception = await act.Should().ThrowAsync<ForbiddenException>();
            exception.Which.ErrorCode.Should().Be(expectedErrorCode);
        }

        // TEST-07: Stop before repository access when the login request fails FluentValidation.
        [Fact]
        public async Task ExecuteAsync_WithInvalidRequest_ShouldThrowValidationExceptionFirst()
        {
            var repository = new Mock<IAuthenticationRepository>();
            var useCase = CreateUseCase(
                repository,
                new Mock<IPasswordService>(),
                new Mock<IRecommendationService>(),
                new Mock<IUnitOfWork>());

            var act = () => useCase.ExecuteAsync(
                new LoginRequestDto { Email = "invalid-email", Password = string.Empty },
                CancellationToken.None);

            await act.Should().ThrowAsync<ValidationException>();
            repository.Verify(
                x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // TEST-08: Return an unknown survey requirement when the recommendation service is unavailable.
        [Fact]
        public async Task ExecuteAsync_WhenSurveyStatusIsUnavailable_ShouldReturnNullPreferenceRequirement()
        {
            var user = CreateUser();
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            var passwordService = new Mock<IPasswordService>();
            passwordService.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            var recommendationService = new Mock<IRecommendationService>();
            recommendationService.Setup(x => x.GetSurveyCompletionAsync(user.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync((bool?)null);
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);
            var useCase = CreateUseCase(repository, passwordService, recommendationService, unitOfWork);

            var result = await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            result.User.NeedsPreferenceSurvey.Should().BeNull();
        }

        // TEST-09: Skip the recommendation survey lookup for a non-traveler account.
        [Fact]
        public async Task ExecuteAsync_WithNonTravelerRole_ShouldNotRequestSurveyStatus()
        {
            var user = CreateUser();
            user.Role = new Role { Id = 2, Code = "RESTAURANT_OWNER", Name = "Restaurant Owner" };
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            var passwordService = new Mock<IPasswordService>();
            passwordService.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            var recommendationService = new Mock<IRecommendationService>();
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);
            var useCase = CreateUseCase(repository, passwordService, recommendationService, unitOfWork);

            var result = await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            result.User.NeedsPreferenceSurvey.Should().BeNull();
            recommendationService.Verify(
                x => x.GetSurveyCompletionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        private static LoginUseCase CreateUseCase(
            Mock<IAuthenticationRepository> repository,
            Mock<IPasswordService> passwordService,
            Mock<IRecommendationService> recommendationService,
            Mock<IUnitOfWork> unitOfWork,
            Mock<IAuditLogService>? auditLogService = null)
        {
            var tokenService = new Mock<IAuthenticationTokenService>();
            tokenService.Setup(x => x.CreateTokens(It.IsAny<User>())).Returns(new AuthenticationTokens(
                "access-token",
                Now.AddMinutes(15),
                "refresh-token",
                "refresh-hash",
                Now.AddDays(30)));

            var currentUserService = new Mock<ICurrentUserService>();
            currentUserService.SetupGet(x => x.UserAgent).Returns("test-agent");
            currentUserService.SetupGet(x => x.IpAddress).Returns(IPAddress.Loopback);

            return new LoginUseCase(
                new LoginRequestDtoValidator(),
                repository.Object,
                passwordService.Object,
                tokenService.Object,
                recommendationService.Object,
                currentUserService.Object,
                (auditLogService ?? new Mock<IAuditLogService>()).Object,
                unitOfWork.Object,
                new FixedTimeProvider(Now));
        }

        private static User CreateUser()
        {
            return new User
            {
                Id = 12,
                Email = "user@angi.test",
                PasswordHash = "password-hash",
                DisplayName = "Test User",
                Status = UserStatus.Active,
                EmailVerifiedAt = Now.AddDays(-1),
                Role = new Role { Id = 1, Code = "TRAVELER", Name = "Traveler" }
            };
        }

        private static LoginRequestDto ValidRequest()
        {
            return new LoginRequestDto
            {
                Email = "user@angi.test",
                Password = "correct-password"
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
    }
}
