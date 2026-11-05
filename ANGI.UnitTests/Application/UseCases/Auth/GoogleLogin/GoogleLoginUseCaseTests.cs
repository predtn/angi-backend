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
using ANGI.Application.UseCases.Auth.GoogleLogin;
using ANGI.Application.UseCases.Validators.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Auth.GoogleLogin
{
    /// <summary>Verifies GoogleLoginUseCase signs in, links or registers Google identities safely and rejects blocked accounts.</summary>
    public sealed class GoogleLoginUseCaseTests
    {
        private static readonly DateTime Now = new(2026, 10, 14, 3, 0, 0, DateTimeKind.Utc);

        // TEST-01: Create an active Traveler, Google link, session, and registration/login audits in one transaction.
        /// <summary>Verifies the complete first-time Google sign-in result and all durable side effects.</summary>
        [Fact]
        public async Task ExecuteAsync_WithNewTraveler_ShouldCreateAccountAndSession()
        {
            // STEP-01: Create harness through CreateHarness.
            var harness = CreateHarness(new GoogleIdentity("google-sub", " New.User@Gmail.com ", null));
            // STEP-02: Stub harness.GetRoleByCodeAsync, harness.ReserveUserIdAsync and harness.GetSurveyStatusAsync.
            harness.Repository.Setup(x => x.GetRoleByCodeAsync("TRAVELER", It.IsAny<CancellationToken>()))
                .ReturnsAsync(TravelerRole());
            harness.Repository.Setup(x => x.ReserveUserIdAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(12);
            harness.RecommendationService.Setup(x => x.GetSurveyStatusAsync(12, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SurveyStatus(false, null, 0));
            // STEP-03: Declare addedUser to capture a call argument, declare addedLogin to capture a call argument and 2 more.
            User? addedUser = null;
            UserExternalLogin? addedLogin = null;
            UserSession? addedSession = null;
            var auditEntries = new List<AuditLogEntry>();
            // STEP-04: Stub harness.AddUser, harness.AddExternalLogin, harness.AddSession and harness.Add.
            harness.Repository.Setup(x => x.AddUser(It.IsAny<User>()))
                .Callback<User>(user => addedUser = user);
            harness.Repository.Setup(x => x.AddExternalLogin(It.IsAny<UserExternalLogin>()))
                .Callback<UserExternalLogin>(login => addedLogin = login);
            harness.Repository.Setup(x => x.AddSession(It.IsAny<UserSession>()))
                .Callback<UserSession>(session => addedSession = session);
            harness.AuditLogService.Setup(x => x.Add(It.IsAny<AuditLogEntry>()))
                .Callback<AuditLogEntry>(auditEntries.Add);

            // STEP-05: Run harness.UseCase.ExecuteAsync with GoogleLoginRequestDto { IdToken = a test value, Role = "TRAVELER" } and keep result.
            var result = await harness.UseCase.ExecuteAsync(new GoogleLoginRequestDto
            {
                IdToken = "signed-token",
                Role = "TRAVELER"
            }, CancellationToken.None);

            // STEP-06: Assert result.IsNewUser is true, result.User.Id is 12, result.User.Email is "new.user@gmail.com", result.User.Status is "active" and 1 more.
            result.IsNewUser.Should().BeTrue();
            result.User.Id.Should().Be(12);
            result.User.Email.Should().Be("new.user@gmail.com");
            result.User.Status.Should().Be("active");
            result.User.NeedsPreferenceSurvey.Should().BeTrue();
            // STEP-07: Assert addedUser is not null, addedUser.DisplayName is "new.user", addedUser.PasswordHash is null and addedUser.EmailVerifiedAt is Now.
            addedUser.Should().NotBeNull();
            addedUser!.DisplayName.Should().Be("new.user");
            addedUser.PasswordHash.Should().BeNull();
            addedUser.EmailVerifiedAt.Should().Be(Now);
            // STEP-08: Assert addedLogin.User is the same instance as addedUser, addedLogin.Provider is ExternalLoginProvider.Google and 1 more.
            addedLogin!.User.Should().BeSameAs(addedUser);
            addedLogin.Provider.Should().Be(ExternalLoginProvider.Google);
            addedLogin.ProviderUserId.Should().Be("google-sub");
            // STEP-09: Assert addedSession.User is the same instance as addedUser, addedSession.RefreshTokenHash is a test value and 1 more.
            addedSession!.User.Should().BeSameAs(addedUser);
            addedSession.RefreshTokenHash.Should().Be("refresh-hash");
            addedSession.IpAddress.Should().Be(IPAddress.Loopback);
            // STEP-10: Assert auditEntries.Select(filtered) equals AuditActions.UserRegistered and auditEntries only holds items matching the condition.
            auditEntries.Select(entry => entry.Action)
                .Should().Equal(AuditActions.UserRegistered, AuditActions.UserLogin);
            auditEntries.Should().OnlyContain(entry =>
                entry.ActorId == 12 && entry.SubjectUserId == 12 && entry.EntityId == 12);
            // STEP-11: Verify CreateRefreshToken is called once, CreateAccessToken is called once, SaveChangesAsync is called once and CommitAsync is called once.
            harness.TokenService.Verify(x => x.CreateRefreshToken(), Times.Once);
            harness.TokenService.Verify(
                x => x.CreateAccessToken(It.Is<User>(user => user.Id == 12), It.IsAny<long>()),
                Times.Once);
            harness.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
            harness.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-02: Sign in an already linked identity without requiring role or creating another link.
        /// <summary>Verifies repeat Google login uses the provider subject as its primary lookup.</summary>
        [Fact]
        public async Task ExecuteAsync_WithLinkedIdentity_ShouldReuseAccount()
        {
            // STEP-01: Create user through ActiveUser and create harness through CreateHarness.
            var user = ActiveUser();
            var harness = CreateHarness();
            // STEP-02: Stub harness.GetUserByExternalLoginAsync.
            harness.Repository.Setup(x => x.GetUserByExternalLoginAsync(
                    ExternalLoginProvider.Google,
                    "google-sub",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);

            // STEP-03: Run harness.UseCase.ExecuteAsync with GoogleLoginRequestDto { IdToken = a test value } and keep result.
            var result = await harness.UseCase.ExecuteAsync(
                new GoogleLoginRequestDto { IdToken = "signed-token" },
                CancellationToken.None);

            // STEP-04: Assert result.IsNewUser is false, result.User.Id is user.Id and user.LastLoginAt is Now.
            result.IsNewUser.Should().BeFalse();
            result.User.Id.Should().Be(user.Id);
            user.LastLoginAt.Should().Be(Now);
            // STEP-05: Verify GetUserByEmailAsync is never called, AddExternalLogin is never called and AddUser is never called.
            harness.Repository.Verify(x => x.GetUserByEmailAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            harness.Repository.Verify(x => x.AddExternalLogin(It.IsAny<UserExternalLogin>()), Times.Never);
            harness.Repository.Verify(x => x.AddUser(It.IsAny<User>()), Times.Never);
        }

        // TEST-03: Link a password account by normalized email and trust Google's verified email claim.
        /// <summary>Verifies Google activates a matching pending account without creating a duplicate user.</summary>
        [Fact]
        public async Task ExecuteAsync_WithPendingPasswordAccount_ShouldLinkAndActivateAccount()
        {
            // STEP-01: Create user through ActiveUser, set user.Status to UserStatus.PendingVerification, set user.EmailVerifiedAt to null and 3 more.
            var user = ActiveUser();
            user.Status = UserStatus.PendingVerification;
            user.EmailVerifiedAt = null;
            user.PasswordHash = "password-chosen-by-whoever-registered";
            var openSession = new UserSession { Id = 40, UserId = user.Id, ExpiresAt = Now.AddDays(1) };
            var harness = CreateHarness(new GoogleIdentity("google-sub", " USER@ANGI.TEST ", "Google Name"));
            // STEP-02: Declare addedLogin to capture a call argument; stub harness.GetUserByEmailAsync, harness.GetActiveSessionsAsync and harness.AddExternalLogin.
            harness.Repository.Setup(x => x.GetUserByEmailAsync(
                    "user@angi.test",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            harness.Repository.Setup(x => x.GetActiveSessionsAsync(user.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync([openSession]);
            UserExternalLogin? addedLogin = null;
            harness.Repository.Setup(x => x.AddExternalLogin(It.IsAny<UserExternalLogin>()))
                .Callback<UserExternalLogin>(login => addedLogin = login);

            // STEP-03: Run harness.UseCase.ExecuteAsync with GoogleLoginRequestDto { IdToken = a test value } and keep result.
            var result = await harness.UseCase.ExecuteAsync(
                new GoogleLoginRequestDto { IdToken = "signed-token" },
                CancellationToken.None);

            // STEP-04: Assert result.IsNewUser is false, user.Status is UserStatus.Active, user.EmailVerifiedAt is Now and user.PasswordHash is null.
            result.IsNewUser.Should().BeFalse();
            user.Status.Should().Be(UserStatus.Active);
            user.EmailVerifiedAt.Should().Be(Now);
            user.PasswordHash.Should().BeNull("the password of an unverified registration is not proven to be the owner's");
            // STEP-05: Assert openSession.RevokedAt is Now and addedLogin.User is the same instance as user.
            openSession.RevokedAt.Should().Be(Now);
            addedLogin!.User.Should().BeSameAs(user);
            // STEP-06: Verify Invalidate is called once and AddUser is never called.
            harness.AccountStatusCache.Verify(x => x.Invalidate(user.Id), Times.Once);
            harness.Repository.Verify(x => x.AddUser(It.IsAny<User>()), Times.Never);
        }

        // TEST-04: A verified password account keeps its password and sessions when Google is linked.
        /// <summary>Verifies only unverified registrations lose their password on the first Google link.</summary>
        [Fact]
        public async Task ExecuteAsync_WithVerifiedPasswordAccount_ShouldKeepPasswordAndSessions()
        {
            // STEP-01: Create user through ActiveUser, set user.PasswordHash to a test value and create harness through CreateHarness.
            var user = ActiveUser();
            user.PasswordHash = "owner-password";
            var harness = CreateHarness();
            // STEP-02: Stub harness.GetUserByEmailAsync.
            harness.Repository.Setup(x => x.GetUserByEmailAsync("user@angi.test", It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);

            // STEP-03: Run harness.UseCase.ExecuteAsync with GoogleLoginRequestDto { IdToken = a test value }.
            await harness.UseCase.ExecuteAsync(new GoogleLoginRequestDto { IdToken = "signed-token" }, CancellationToken.None);

            // STEP-04: Assert user.PasswordHash is a test value and user.EmailVerifiedAt is Now.AddDays().
            user.PasswordHash.Should().Be("owner-password");
            user.EmailVerifiedAt.Should().Be(Now.AddDays(-1));
            // STEP-05: Verify GetActiveSessionsAsync is never called.
            harness.Repository.Verify(x => x.GetActiveSessionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-05: Require a role only when no linked or email-matching account exists.
        /// <summary>Verifies first-time sign-in returns ROLE_REQUIRED without writing partial data.</summary>
        [Fact]
        public async Task ExecuteAsync_WithNewIdentityAndNoRole_ShouldThrowRoleRequired()
        {
            // STEP-01: Create harness through CreateHarness.
            var harness = CreateHarness();

            // STEP-02: Wrap harness.UseCase.ExecuteAsync with GoogleLoginRequestDto { IdToken = a test value } in act so its outcome can be asserted.
            var act = () => harness.UseCase.ExecuteAsync(
                new GoogleLoginRequestDto { IdToken = "signed-token" },
                CancellationToken.None);

            // STEP-03: Assert act throws BadRequestException and exception.ErrorCode is "ROLE_REQUIRED".
            var exception = await act.Should().ThrowAsync<BadRequestException>();
            exception.Which.ErrorCode.Should().Be("ROLE_REQUIRED");
            // STEP-04: Verify AddUser is never called, AddSession is never called, SaveChangesAsync is never called and CommitAsync is never called.
            harness.Repository.Verify(x => x.AddUser(It.IsAny<User>()), Times.Never);
            harness.Repository.Verify(x => x.AddSession(It.IsAny<UserSession>()), Times.Never);
            harness.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
            harness.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-06: Reject every linked account state that must not receive a new session.
        /// <summary>Verifies blocked account states retain the established authentication error codes.</summary>
        [Theory]
        [InlineData(UserStatus.Suspended, "ACCOUNT_SUSPENDED")]
        [InlineData(UserStatus.Banned, "ACCOUNT_BANNED")]
        [InlineData(UserStatus.Deactivated, "ACCOUNT_DEACTIVATED")]
        public async Task ExecuteAsync_WithBlockedLinkedAccount_ShouldThrowDocumentedError(
            UserStatus status,
            string expectedCode)
        {
            // STEP-01: Create user through ActiveUser, set user.Status to status, set user.SuspendedUntil to Suspended ? Now.AddDays(1) : null and 1 more.
            var user = ActiveUser();
            user.Status = status;
            user.SuspendedUntil = status == UserStatus.Suspended ? Now.AddDays(1) : null;
            var harness = CreateHarness();
            // STEP-02: Stub harness.GetUserByExternalLoginAsync.
            harness.Repository.Setup(x => x.GetUserByExternalLoginAsync(
                    ExternalLoginProvider.Google,
                    "google-sub",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);

            // STEP-03: Wrap harness.UseCase.ExecuteAsync with GoogleLoginRequestDto { IdToken = a test value } in act so its outcome can be asserted.
            var act = () => harness.UseCase.ExecuteAsync(
                new GoogleLoginRequestDto { IdToken = "signed-token" },
                CancellationToken.None);

            // STEP-04: Assert act throws AppException and exception.ErrorCode is expectedCode.
            var exception = await act.Should().ThrowAsync<AppException>();
            exception.Which.ErrorCode.Should().Be(expectedCode);
            // STEP-05: Verify AddSession is never called and SaveChangesAsync is never called.
            harness.Repository.Verify(x => x.AddSession(It.IsAny<UserSession>()), Times.Never);
            harness.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-07: Validate the request before calling Google or opening a database transaction.
        /// <summary>Verifies FluentValidation remains the first operation in AUTH-05.</summary>
        [Fact]
        public async Task ExecuteAsync_WithInvalidRequest_ShouldFailValidationFirst()
        {
            // STEP-01: Create harness through CreateHarness.
            var harness = CreateHarness();

            // STEP-02: Wrap harness.UseCase.ExecuteAsync with an empty GoogleLoginRequestDto in act so its outcome can be asserted.
            var act = () => harness.UseCase.ExecuteAsync(new GoogleLoginRequestDto(), CancellationToken.None);

            // STEP-03: Assert act throws ValidationException.
            await act.Should().ThrowAsync<ValidationException>();
            // STEP-04: Verify VerifyIdTokenAsync is never called and BeginTransactionAsync is never called.
            harness.GoogleIdentityService.Verify(x => x.VerifyIdTokenAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            harness.UnitOfWork.Verify(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-08: Skip preference lookup for Restaurant Owners while still returning a complete session.
        /// <summary>Verifies AUTH-05 applies the same role-specific survey behavior as password login.</summary>
        [Fact]
        public async Task ExecuteAsync_WithRestaurantOwner_ShouldNotRequestSurveyStatus()
        {
            // STEP-01: Create user through ActiveUser and create harness through CreateHarness.
            var user = ActiveUser("RESTAURANT_OWNER");
            var harness = CreateHarness();
            // STEP-02: Stub harness.GetUserByExternalLoginAsync.
            harness.Repository.Setup(x => x.GetUserByExternalLoginAsync(
                    ExternalLoginProvider.Google,
                    "google-sub",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);

            // STEP-03: Run harness.UseCase.ExecuteAsync with GoogleLoginRequestDto { IdToken = a test value } and keep result.
            var result = await harness.UseCase.ExecuteAsync(
                new GoogleLoginRequestDto { IdToken = "signed-token" },
                CancellationToken.None);

            // STEP-04: Assert result.User.NeedsPreferenceSurvey is null.
            result.User.NeedsPreferenceSurvey.Should().BeNull();
            // STEP-05: Verify GetSurveyStatusAsync is never called.
            harness.RecommendationService.Verify(x => x.GetSurveyStatusAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-09: Preserve successful login when the recommendation service cannot provide survey state.
        /// <summary>Verifies an unavailable optional Reco dependency produces a nullable survey flag.</summary>
        [Fact]
        public async Task ExecuteAsync_WhenSurveyStatusIsUnavailable_ShouldReturnNullSurveyFlag()
        {
            // STEP-01: Create user through ActiveUser and create harness through CreateHarness.
            var user = ActiveUser();
            var harness = CreateHarness();
            // STEP-02: Stub harness.GetUserByExternalLoginAsync and harness.GetSurveyStatusAsync.
            harness.Repository.Setup(x => x.GetUserByExternalLoginAsync(
                    ExternalLoginProvider.Google,
                    "google-sub",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            harness.RecommendationService.Setup(x => x.GetSurveyStatusAsync(
                    user.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((SurveyStatus?)null);

            // STEP-03: Run harness.UseCase.ExecuteAsync with GoogleLoginRequestDto { IdToken = a test value } and keep result.
            var result = await harness.UseCase.ExecuteAsync(
                new GoogleLoginRequestDto { IdToken = "signed-token" },
                CancellationToken.None);

            // STEP-04: Assert result.User.NeedsPreferenceSurvey is null.
            result.User.NeedsPreferenceSurvey.Should().BeNull();
        }

        // TEST-10: Bound an oversized Google profile name to the users.display_name column length.
        /// <summary>Verifies external profile data cannot cause a database length violation during registration.</summary>
        [Fact]
        public async Task ExecuteAsync_WithOversizedGoogleName_ShouldTruncateDisplayName()
        {
            // STEP-01: Create harness through CreateHarness.
            var harness = CreateHarness(new GoogleIdentity(
                "google-sub",
                "user@angi.test",
                new string('N', 101)));
            // STEP-02: Declare addedUser to capture a call argument; stub harness.GetRoleByCodeAsync, harness.ReserveUserIdAsync and harness.AddUser.
            harness.Repository.Setup(x => x.GetRoleByCodeAsync("TRAVELER", It.IsAny<CancellationToken>()))
                .ReturnsAsync(TravelerRole());
            harness.Repository.Setup(x => x.ReserveUserIdAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(12);
            User? addedUser = null;
            harness.Repository.Setup(x => x.AddUser(It.IsAny<User>()))
                .Callback<User>(user => addedUser = user);

            // STEP-03: Run harness.UseCase.ExecuteAsync with GoogleLoginRequestDto { IdToken = a test value, Role = "TRAVELER" }.
            await harness.UseCase.ExecuteAsync(new GoogleLoginRequestDto
            {
                IdToken = "signed-token",
                Role = "TRAVELER"
            }, CancellationToken.None);

            // STEP-04: Assert addedUser.DisplayName has length 100.
            addedUser!.DisplayName.Should().HaveLength(100);
        }

        /// <summary>Creates a fully configured use-case harness with replaceable mocked boundaries.</summary>
        private static Harness CreateHarness(GoogleIdentity? identity = null)
        {
            var googleIdentityService = new Mock<IGoogleIdentityService>();
            googleIdentityService.Setup(x => x.VerifyIdTokenAsync("signed-token", It.IsAny<CancellationToken>()))
                .ReturnsAsync(identity ?? new GoogleIdentity("google-sub", "user@angi.test", "Test User"));
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.LockExternalLoginAsync(
                    ExternalLoginProvider.Google,
                    "google-sub",
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var tokenService = new Mock<IAuthenticationTokenService>();
            tokenService.Setup(x => x.CreateRefreshToken())
                .Returns(new GeneratedRefreshToken(
                    "refresh-token",
                    "refresh-hash",
                    Now.AddDays(30)));
            tokenService.Setup(x => x.CreateAccessToken(It.IsAny<User>(), It.IsAny<long>()))
                .Returns(new IssuedAccessToken("access-token", Now.AddMinutes(15)));
            var recommendationService = new Mock<IRecommendationService>();
            var currentUserService = new Mock<ICurrentUserService>();
            currentUserService.SetupGet(x => x.UserAgent).Returns("test-agent");
            currentUserService.SetupGet(x => x.IpAddress).Returns(IPAddress.Loopback);
            var accountStatusCache = new Mock<IAccountStatusCache>();
            var auditLogService = new Mock<IAuditLogService>();
            var transaction = new Mock<IUnitOfWorkTransaction>();
            transaction.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            transaction.Setup(x => x.DisposeAsync()).Returns(ValueTask.CompletedTask);
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(transaction.Object);
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);

            var useCase = new GoogleLoginUseCase(
                new GoogleLoginRequestDtoValidator(),
                googleIdentityService.Object,
                repository.Object,
                tokenService.Object,
                recommendationService.Object,
                currentUserService.Object,
                accountStatusCache.Object,
                auditLogService.Object,
                unitOfWork.Object,
                new FixedTimeProvider(Now));
            return new Harness(
                useCase,
                googleIdentityService,
                repository,
                tokenService,
                recommendationService,
                accountStatusCache,
                auditLogService,
                unitOfWork,
                transaction);
        }

        /// <summary>Creates an active user with all navigation data required by token and response mapping.</summary>
        private static User ActiveUser(string roleCode = "TRAVELER") => new()
        {
            Id = 12,
            Email = "user@angi.test",
            DisplayName = "Test User",
            EmailVerifiedAt = Now.AddDays(-1),
            Status = UserStatus.Active,
            Role = new Role
            {
                Id = roleCode == "TRAVELER" ? (short)1 : (short)2,
                Code = roleCode,
                Name = roleCode
            }
        };

        /// <summary>Creates the seeded Traveler role used by first-login tests.</summary>
        private static Role TravelerRole() => new() { Id = 1, Code = "TRAVELER", Name = "Traveler" };

        /// <summary>Groups the use case under test with the mocks each scenario configures and verifies.</summary>
        private sealed record Harness(
            GoogleLoginUseCase UseCase,
            Mock<IGoogleIdentityService> GoogleIdentityService,
            Mock<IAuthenticationRepository> Repository,
            Mock<IAuthenticationTokenService> TokenService,
            Mock<IRecommendationService> RecommendationService,
            Mock<IAccountStatusCache> AccountStatusCache,
            Mock<IAuditLogService> AuditLogService,
            Mock<IUnitOfWork> UnitOfWork,
            Mock<IUnitOfWorkTransaction> Transaction);

        /// <summary>Clock fixed at one instant so session and audit timestamps are deterministic.</summary>
        private sealed class FixedTimeProvider : TimeProvider
        {
            private readonly DateTimeOffset _now;

            /// <summary>Initializes the deterministic UTC clock used by AUTH-05 tests.</summary>
            public FixedTimeProvider(DateTime now)
            {
                _now = new DateTimeOffset(now);
            }

            /// <summary>Returns the fixed instant for every test call.</summary>
            public override DateTimeOffset GetUtcNow() => _now;
        }
    }
}
