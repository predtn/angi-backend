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
    public sealed class GoogleLoginUseCaseTests
    {
        private static readonly DateTime Now = new(2026, 10, 14, 3, 0, 0, DateTimeKind.Utc);

        // TEST-01: Create an active Traveler, Google link, session, and registration/login audits in one transaction.
        /// <summary>Verifies the complete first-time Google sign-in result and all durable side effects.</summary>
        [Fact]
        public async Task ExecuteAsync_WithNewTraveler_ShouldCreateAccountAndSession()
        {
            var harness = CreateHarness(new GoogleIdentity("google-sub", " New.User@Gmail.com ", null));
            harness.Repository.Setup(x => x.GetRoleByCodeAsync("TRAVELER", It.IsAny<CancellationToken>()))
                .ReturnsAsync(TravelerRole());
            harness.Repository.Setup(x => x.ReserveUserIdAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(12);
            harness.RecommendationService.Setup(x => x.GetSurveyStatusAsync(12, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SurveyStatus(false, null, 0));
            User? addedUser = null;
            UserExternalLogin? addedLogin = null;
            UserSession? addedSession = null;
            var auditEntries = new List<AuditLogEntry>();
            harness.Repository.Setup(x => x.AddUser(It.IsAny<User>()))
                .Callback<User>(user => addedUser = user);
            harness.Repository.Setup(x => x.AddExternalLogin(It.IsAny<UserExternalLogin>()))
                .Callback<UserExternalLogin>(login => addedLogin = login);
            harness.Repository.Setup(x => x.AddSession(It.IsAny<UserSession>()))
                .Callback<UserSession>(session => addedSession = session);
            harness.AuditLogService.Setup(x => x.Add(It.IsAny<AuditLogEntry>()))
                .Callback<AuditLogEntry>(auditEntries.Add);

            var result = await harness.UseCase.ExecuteAsync(new GoogleLoginRequestDto
            {
                IdToken = "signed-token",
                Role = "TRAVELER"
            }, CancellationToken.None);

            result.IsNewUser.Should().BeTrue();
            result.User.Id.Should().Be(12);
            result.User.Email.Should().Be("new.user@gmail.com");
            result.User.Status.Should().Be("active");
            result.User.NeedsPreferenceSurvey.Should().BeTrue();
            addedUser.Should().NotBeNull();
            addedUser!.DisplayName.Should().Be("new.user");
            addedUser.PasswordHash.Should().BeNull();
            addedUser.EmailVerifiedAt.Should().Be(Now);
            addedLogin!.User.Should().BeSameAs(addedUser);
            addedLogin.Provider.Should().Be(ExternalLoginProvider.Google);
            addedLogin.ProviderUserId.Should().Be("google-sub");
            addedSession!.User.Should().BeSameAs(addedUser);
            addedSession.RefreshTokenHash.Should().Be("refresh-hash");
            addedSession.IpAddress.Should().Be(IPAddress.Loopback);
            auditEntries.Select(entry => entry.Action)
                .Should().Equal(AuditActions.UserRegistered, AuditActions.UserLogin);
            auditEntries.Should().OnlyContain(entry =>
                entry.ActorId == 12 && entry.SubjectUserId == 12 && entry.EntityId == 12);
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
            var user = ActiveUser();
            var harness = CreateHarness();
            harness.Repository.Setup(x => x.GetUserByExternalLoginAsync(
                    ExternalLoginProvider.Google,
                    "google-sub",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);

            var result = await harness.UseCase.ExecuteAsync(
                new GoogleLoginRequestDto { IdToken = "signed-token" },
                CancellationToken.None);

            result.IsNewUser.Should().BeFalse();
            result.User.Id.Should().Be(user.Id);
            user.LastLoginAt.Should().Be(Now);
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
            var user = ActiveUser();
            user.Status = UserStatus.PendingVerification;
            user.EmailVerifiedAt = null;
            var harness = CreateHarness(new GoogleIdentity("google-sub", " USER@ANGI.TEST ", "Google Name"));
            harness.Repository.Setup(x => x.GetUserByEmailAsync(
                    "user@angi.test",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            UserExternalLogin? addedLogin = null;
            harness.Repository.Setup(x => x.AddExternalLogin(It.IsAny<UserExternalLogin>()))
                .Callback<UserExternalLogin>(login => addedLogin = login);

            var result = await harness.UseCase.ExecuteAsync(
                new GoogleLoginRequestDto { IdToken = "signed-token" },
                CancellationToken.None);

            result.IsNewUser.Should().BeFalse();
            user.Status.Should().Be(UserStatus.Active);
            user.EmailVerifiedAt.Should().Be(Now);
            addedLogin!.User.Should().BeSameAs(user);
            harness.AccountStatusCache.Verify(x => x.Invalidate(user.Id), Times.Once);
            harness.Repository.Verify(x => x.AddUser(It.IsAny<User>()), Times.Never);
        }

        // TEST-04: Require a role only when no linked or email-matching account exists.
        /// <summary>Verifies first-time sign-in returns ROLE_REQUIRED without writing partial data.</summary>
        [Fact]
        public async Task ExecuteAsync_WithNewIdentityAndNoRole_ShouldThrowRoleRequired()
        {
            var harness = CreateHarness();

            var act = () => harness.UseCase.ExecuteAsync(
                new GoogleLoginRequestDto { IdToken = "signed-token" },
                CancellationToken.None);

            var exception = await act.Should().ThrowAsync<BadRequestException>();
            exception.Which.ErrorCode.Should().Be("ROLE_REQUIRED");
            harness.Repository.Verify(x => x.AddUser(It.IsAny<User>()), Times.Never);
            harness.Repository.Verify(x => x.AddSession(It.IsAny<UserSession>()), Times.Never);
            harness.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
            harness.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-05: Reject every linked account state that must not receive a new session.
        /// <summary>Verifies blocked account states retain the established authentication error codes.</summary>
        [Theory]
        [InlineData(UserStatus.Suspended, "ACCOUNT_SUSPENDED")]
        [InlineData(UserStatus.Banned, "ACCOUNT_BANNED")]
        [InlineData(UserStatus.Deactivated, "ACCOUNT_DEACTIVATED")]
        public async Task ExecuteAsync_WithBlockedLinkedAccount_ShouldThrowDocumentedError(
            UserStatus status,
            string expectedCode)
        {
            var user = ActiveUser();
            user.Status = status;
            user.SuspendedUntil = status == UserStatus.Suspended ? Now.AddDays(1) : null;
            var harness = CreateHarness();
            harness.Repository.Setup(x => x.GetUserByExternalLoginAsync(
                    ExternalLoginProvider.Google,
                    "google-sub",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);

            var act = () => harness.UseCase.ExecuteAsync(
                new GoogleLoginRequestDto { IdToken = "signed-token" },
                CancellationToken.None);

            var exception = await act.Should().ThrowAsync<AppException>();
            exception.Which.ErrorCode.Should().Be(expectedCode);
            harness.Repository.Verify(x => x.AddSession(It.IsAny<UserSession>()), Times.Never);
            harness.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-06: Validate the request before calling Google or opening a database transaction.
        /// <summary>Verifies FluentValidation remains the first operation in AUTH-05.</summary>
        [Fact]
        public async Task ExecuteAsync_WithInvalidRequest_ShouldFailValidationFirst()
        {
            var harness = CreateHarness();

            var act = () => harness.UseCase.ExecuteAsync(new GoogleLoginRequestDto(), CancellationToken.None);

            await act.Should().ThrowAsync<ValidationException>();
            harness.GoogleIdentityService.Verify(x => x.VerifyIdTokenAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            harness.UnitOfWork.Verify(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-07: Skip preference lookup for Restaurant Owners while still returning a complete session.
        /// <summary>Verifies AUTH-05 applies the same role-specific survey behavior as password login.</summary>
        [Fact]
        public async Task ExecuteAsync_WithRestaurantOwner_ShouldNotRequestSurveyStatus()
        {
            var user = ActiveUser("RESTAURANT_OWNER");
            var harness = CreateHarness();
            harness.Repository.Setup(x => x.GetUserByExternalLoginAsync(
                    ExternalLoginProvider.Google,
                    "google-sub",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);

            var result = await harness.UseCase.ExecuteAsync(
                new GoogleLoginRequestDto { IdToken = "signed-token" },
                CancellationToken.None);

            result.User.NeedsPreferenceSurvey.Should().BeNull();
            harness.RecommendationService.Verify(x => x.GetSurveyStatusAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-08: Preserve successful login when the recommendation service cannot provide survey state.
        /// <summary>Verifies an unavailable optional Reco dependency produces a nullable survey flag.</summary>
        [Fact]
        public async Task ExecuteAsync_WhenSurveyStatusIsUnavailable_ShouldReturnNullSurveyFlag()
        {
            var user = ActiveUser();
            var harness = CreateHarness();
            harness.Repository.Setup(x => x.GetUserByExternalLoginAsync(
                    ExternalLoginProvider.Google,
                    "google-sub",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            harness.RecommendationService.Setup(x => x.GetSurveyStatusAsync(
                    user.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((SurveyStatus?)null);

            var result = await harness.UseCase.ExecuteAsync(
                new GoogleLoginRequestDto { IdToken = "signed-token" },
                CancellationToken.None);

            result.User.NeedsPreferenceSurvey.Should().BeNull();
        }

        // TEST-09: Bound an oversized Google profile name to the users.display_name column length.
        /// <summary>Verifies external profile data cannot cause a database length violation during registration.</summary>
        [Fact]
        public async Task ExecuteAsync_WithOversizedGoogleName_ShouldTruncateDisplayName()
        {
            var harness = CreateHarness(new GoogleIdentity(
                "google-sub",
                "user@angi.test",
                new string('N', 101)));
            harness.Repository.Setup(x => x.GetRoleByCodeAsync("TRAVELER", It.IsAny<CancellationToken>()))
                .ReturnsAsync(TravelerRole());
            harness.Repository.Setup(x => x.ReserveUserIdAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(12);
            User? addedUser = null;
            harness.Repository.Setup(x => x.AddUser(It.IsAny<User>()))
                .Callback<User>(user => addedUser = user);

            await harness.UseCase.ExecuteAsync(new GoogleLoginRequestDto
            {
                IdToken = "signed-token",
                Role = "TRAVELER"
            }, CancellationToken.None);

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
