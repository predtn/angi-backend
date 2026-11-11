using System.Net;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.Common.Models.Auth;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Auth.Login;
using ANGI.Application.UseCases.Validators.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Auth.Login
{
    /// <summary>Verifies LoginUseCase issues sessions only for active accounts with a matching password and returns the survey requirement.</summary>
    public sealed class LoginUseCaseTests
    {
        private static readonly DateTime Now = new(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);

        // TEST-01: Create a session and return the documented authentication result for valid credentials.
        /// <summary>Ensures a valid login creates exactly one session and returns the documented auth payload.</summary>
        [Fact]
        public async Task ExecuteAsync_WithValidCredentials_ShouldCreateSessionAndReturnAuthResult()
        {
            // STEP-01: Create user through CreateUser.
            var user = CreateUser();
            // STEP-02: Declare addedSession to capture a call argument and 1 more; mock IAuthenticationRepository and 4 more; stub repository.GetUserByEmailAsync and 5 more.
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
            recommendationService.Setup(x => x.GetSurveyStatusAsync(user.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SurveyStatus(false, null, 0));
            var unitOfWork = new Mock<IUnitOfWork>();
            // The database assigns the session id on save.
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .Callback(() => addedSession!.Id = 77)
                .ReturnsAsync(3);

            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(
                repository,
                passwordService,
                recommendationService,
                unitOfWork,
                auditLogService);

            // STEP-04: Run useCase.ExecuteAsync with LoginRequestDto { Email = " User@ANGI.Test ", Password = a test value } and keep result.
            var result = await useCase.ExecuteAsync(new LoginRequestDto
            {
                Email = "  User@ANGI.Test ",
                Password = "correct-password"
            }, CancellationToken.None);

            // STEP-05: Assert result.AccessToken is a test value, result.RefreshToken is a test value, result.User.Id is user.Id and 3 more.
            // Issued after the save, carrying the new session's id as claim sid.
            result.AccessToken.Should().Be("access-token-sid-77");
            result.RefreshToken.Should().Be("refresh-token");
            result.User.Id.Should().Be(user.Id);
            result.User.Role.Should().Be("TRAVELER");
            result.User.Status.Should().Be("active");
            result.User.NeedsPreferenceSurvey.Should().BeTrue();
            // STEP-06: Assert user.LastLoginAt is Now, addedSession is not null, addedSession.RefreshTokenHash is a test value and 1 more.
            user.LastLoginAt.Should().Be(Now);
            addedSession.Should().NotBeNull();
            addedSession!.RefreshTokenHash.Should().Be("refresh-hash");
            addedSession.IpAddress.Should().Be(IPAddress.Loopback);
            // STEP-07: Assert addedAuditLog is not null, addedAuditLog.Action is "USER_LOGIN", addedAuditLog.EntityType is "user" and 4 more.
            addedAuditLog.Should().NotBeNull();
            addedAuditLog!.Action.Should().Be("USER_LOGIN");
            addedAuditLog.EntityType.Should().Be("user");
            addedAuditLog.EntityId.Should().Be(user.Id);
            addedAuditLog.SubjectUserId.Should().Be(user.Id);
            addedAuditLog.ActorId.Should().Be(user.Id);
            addedAuditLog.ActorRole.Should().Be("TRAVELER");
            // STEP-08: Verify SaveChangesAsync is called once.
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-02: Reject an incorrect password without creating a session or saving changes.
        /// <summary>Ensures a wrong password leaves no session or saved change behind.</summary>
        [Fact]
        public async Task ExecuteAsync_WithIncorrectPassword_ShouldThrowInvalidCredentials()
        {
            // STEP-01: Mock IAuthenticationRepository, IPasswordService and IUnitOfWork; stub repository.GetUserByEmailAsync and passwordService.Verify.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateUser());
            var passwordService = new Mock<IPasswordService>();
            passwordService.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
            var unitOfWork = new Mock<IUnitOfWork>();
            // STEP-02: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(
                repository,
                passwordService,
                new Mock<IRecommendationService>(),
                unitOfWork);

            // STEP-03: Wrap useCase.ExecuteAsync with LoginRequestDto { Email = "user@angi.test", Password = a test value } in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(new LoginRequestDto
            {
                Email = "user@angi.test",
                Password = "wrong-password"
            }, CancellationToken.None);

            // STEP-04: Assert act throws UnauthorizedException and exception.ErrorCode is "INVALID_CREDENTIALS".
            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("INVALID_CREDENTIALS");
            // STEP-05: Verify AddSession is never called and SaveChangesAsync is never called.
            repository.Verify(x => x.AddSession(It.IsAny<UserSession>()), Times.Never);
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-03: Reject a suspended account with the endpoint-specific error code.
        /// <summary>Ensures a suspended account cannot sign in and receives the login-specific code.</summary>
        [Fact]
        public async Task ExecuteAsync_WithSuspendedAccount_ShouldThrowAccountSuspended()
        {
            // STEP-01: Create user through CreateUser, set user.Status to UserStatus.Suspended and set user.SuspendedUntil to Now.AddDays().
            var user = CreateUser();
            user.Status = UserStatus.Suspended;
            user.SuspendedUntil = Now.AddDays(1);
            // STEP-02: Mock IAuthenticationRepository and IPasswordService; stub repository.GetUserByEmailAsync and passwordService.Verify.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            var passwordService = new Mock<IPasswordService>();
            passwordService.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(
                repository,
                passwordService,
                new Mock<IRecommendationService>(),
                new Mock<IUnitOfWork>());

            // STEP-04: Wrap useCase.ExecuteAsync with LoginRequestDto { Email = "user@angi.test", Password = a test value } in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(new LoginRequestDto
            {
                Email = "user@angi.test",
                Password = "correct-password"
            }, CancellationToken.None);

            // STEP-05: Assert act throws AccountSuspendedException, exception.ErrorCode is "ACCOUNT_SUSPENDED" and exception.SuspendedUntil is user.SuspendedUntil.
            var exception = await act.Should().ThrowAsync<AccountSuspendedException>();
            exception.Which.ErrorCode.Should().Be("ACCOUNT_SUSPENDED");
            exception.Which.SuspendedUntil.Should().Be(user.SuspendedUntil);
        }

        // TEST-04: Reject an unknown email without verifying a password or persisting login data.
        /// <summary>Prevents account enumeration by answering an unknown email like a wrong password.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUnknownEmail_ShouldThrowInvalidCredentials()
        {
            // STEP-01: Mock IAuthenticationRepository, IPasswordService and IUnitOfWork; stub repository.GetUserByEmailAsync.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);
            var passwordService = new Mock<IPasswordService>();
            var unitOfWork = new Mock<IUnitOfWork>();
            // STEP-02: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, passwordService, new Mock<IRecommendationService>(), unitOfWork);

            // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-04: Assert act throws UnauthorizedException and exception.ErrorCode is "INVALID_CREDENTIALS".
            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("INVALID_CREDENTIALS");
            // STEP-05: Verify Verify is never called and SaveChangesAsync is never called.
            passwordService.Verify(x => x.Verify(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-05: Reject a password-only login for an account that has no password hash.
        /// <summary>Ensures a Google-only account cannot be entered through the password login.</summary>
        [Fact]
        public async Task ExecuteAsync_WithoutPasswordHash_ShouldThrowInvalidCredentials()
        {
            // STEP-01: Create user through CreateUser and set user.PasswordHash to null.
            var user = CreateUser();
            user.PasswordHash = null;
            // STEP-02: Mock IAuthenticationRepository and IPasswordService; stub repository.GetUserByEmailAsync.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            var passwordService = new Mock<IPasswordService>();
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(
                repository,
                passwordService,
                new Mock<IRecommendationService>(),
                new Mock<IUnitOfWork>());

            // STEP-04: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-05: Assert act throws UnauthorizedException and exception.ErrorCode is "INVALID_CREDENTIALS".
            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("INVALID_CREDENTIALS");
            // STEP-06: Verify Verify is never called.
            passwordService.Verify(x => x.Verify(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        // TEST-06: Map every non-active account status to its documented login error code.
        /// <summary>Ensures each blocked account status maps to the error code clients rely on.</summary>
        [Theory]
        [InlineData(UserStatus.PendingVerification, "EMAIL_NOT_VERIFIED")]
        [InlineData(UserStatus.Banned, "ACCOUNT_BANNED")]
        [InlineData(UserStatus.Deactivated, "ACCOUNT_DEACTIVATED")]
        public async Task ExecuteAsync_WithBlockedAccountStatus_ShouldThrowDocumentedError(
            UserStatus status,
            string expectedErrorCode)
        {
            // STEP-01: Create user through CreateUser and set user.Status to status.
            var user = CreateUser();
            user.Status = status;
            // STEP-02: Mock IAuthenticationRepository and IPasswordService; stub repository.GetUserByEmailAsync and passwordService.Verify.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            var passwordService = new Mock<IPasswordService>();
            passwordService.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(
                repository,
                passwordService,
                new Mock<IRecommendationService>(),
                new Mock<IUnitOfWork>());

            // STEP-04: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-05: Assert act throws ForbiddenException and exception.ErrorCode is expectedErrorCode.
            var exception = await act.Should().ThrowAsync<ForbiddenException>();
            exception.Which.ErrorCode.Should().Be(expectedErrorCode);
        }

        // TEST-07: Stop before repository access when the login request fails FluentValidation.
        /// <summary>Ensures malformed input is rejected before any database work.</summary>
        [Fact]
        public async Task ExecuteAsync_WithInvalidRequest_ShouldThrowValidationExceptionFirst()
        {
            // STEP-01: Mock IAuthenticationRepository.
            var repository = new Mock<IAuthenticationRepository>();
            // STEP-02: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(
                repository,
                new Mock<IPasswordService>(),
                new Mock<IRecommendationService>(),
                new Mock<IUnitOfWork>());

            // STEP-03: Wrap useCase.ExecuteAsync with LoginRequestDto { Email = "invalid-email", Password = string.Empty } in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(
                new LoginRequestDto { Email = "invalid-email", Password = string.Empty },
                CancellationToken.None);

            // STEP-04: Assert act throws ValidationException.
            await act.Should().ThrowAsync<ValidationException>();
            // STEP-05: Verify GetUserByEmailAsync is never called.
            repository.Verify(
                x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // TEST-08: Return an unknown survey requirement when the recommendation service is unavailable.
        /// <summary>Ensures a reco outage does not block login and the survey state is reported as unknown.</summary>
        [Fact]
        public async Task ExecuteAsync_WhenSurveyStatusIsUnavailable_ShouldReturnNullPreferenceRequirement()
        {
            // STEP-01: Create user through CreateUser.
            var user = CreateUser();
            // STEP-02: Mock IAuthenticationRepository, IPasswordService and 2 more; stub repository.GetUserByEmailAsync, passwordService.Verify and 2 more.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            var passwordService = new Mock<IPasswordService>();
            passwordService.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            var recommendationService = new Mock<IRecommendationService>();
            recommendationService.Setup(x => x.GetSurveyStatusAsync(user.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync((SurveyStatus?)null);
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, passwordService, recommendationService, unitOfWork);

            // STEP-04: Run useCase.ExecuteAsync and keep result.
            var result = await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-05: Assert result.User.NeedsPreferenceSurvey is null.
            result.User.NeedsPreferenceSurvey.Should().BeNull();
        }

        // TEST-09: Skip the recommendation survey lookup for a non-traveler account.
        /// <summary>Avoids a reco call for roles that never take the preference survey.</summary>
        [Fact]
        public async Task ExecuteAsync_WithNonTravelerRole_ShouldNotRequestSurveyStatus()
        {
            // STEP-01: Create user through CreateUser and set user.Role to a new Role.
            var user = CreateUser();
            user.Role = new Role { Id = 2, Code = "RESTAURANT_OWNER", Name = "Restaurant Owner" };
            // STEP-02: Mock IAuthenticationRepository, IPasswordService and 2 more; stub repository.GetUserByEmailAsync, passwordService.Verify and 1 more.
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            var passwordService = new Mock<IPasswordService>();
            passwordService.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            var recommendationService = new Mock<IRecommendationService>();
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, passwordService, recommendationService, unitOfWork);

            // STEP-04: Run useCase.ExecuteAsync and keep result.
            var result = await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-05: Assert result.User.NeedsPreferenceSurvey is null.
            result.User.NeedsPreferenceSurvey.Should().BeNull();
            // STEP-06: Verify GetSurveyStatusAsync is never called.
            recommendationService.Verify(
                x => x.GetSurveyStatusAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>Creates the use case from the given mocks with a fixed clock and deterministic token issuance.</summary>
        private static LoginUseCase CreateUseCase(
            Mock<IAuthenticationRepository> repository,
            Mock<IPasswordService> passwordService,
            Mock<IRecommendationService> recommendationService,
            Mock<IUnitOfWork> unitOfWork,
            Mock<IAuditLogService>? auditLogService = null)
        {
            var tokenService = new Mock<IAuthenticationTokenService>();
            tokenService.Setup(x => x.CreateRefreshToken())
                .Returns(new GeneratedRefreshToken("refresh-token", "refresh-hash", Now.AddDays(30)));
            tokenService.Setup(x => x.CreateAccessToken(It.IsAny<User>(), It.IsAny<long>()))
                .Returns<User, long>((_, sessionId) => new IssuedAccessToken($"access-token-sid-{sessionId}", Now.AddMinutes(15)));

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

        /// <summary>Creates an active, verified traveler with a password hash for the login scenarios.</summary>
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

        /// <summary>Creates a login request that matches the default user's credentials.</summary>
        private static LoginRequestDto ValidRequest()
        {
            return new LoginRequestDto
            {
                Email = "user@angi.test",
                Password = "correct-password"
            };
        }

        /// <summary>Clock fixed at one instant so session expiry and login timestamps are deterministic.</summary>
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
