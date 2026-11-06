using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.UseCases.Auth.ResetPassword;
using ANGI.Application.UseCases.Validators.Auth;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Auth.ResetPassword
{
    /// <summary>Verifies ResetPasswordUseCase consumes only valid, unexpired reset tokens and revokes every session on success.</summary>
    public sealed class ResetPasswordUseCaseTests
    {
        private static readonly DateTime Now = new(2026, 10, 17, 3, 0, 0, DateTimeKind.Utc);

        // TEST-01: Consume a valid reset token, replace the hash, revoke every active session, and audit the user.
        /// <summary>Verifies all AUTH-09 writes are prepared and committed in one successful transaction.</summary>
        [Fact]
        public async Task ExecuteAsync_WithValidToken_ShouldResetPasswordRevokeSessionsAndWriteAudit()
        {
            // STEP-01: Create userToken through CreateToken, build the sessions collection and get repository from RepositoryReturning.
            var userToken = CreateToken();
            var sessions = new[]
            {
                new UserSession { Id = 10, UserId = userToken.UserId, RefreshTokenHash = "one" },
                new UserSession { Id = 11, UserId = userToken.UserId, RefreshTokenHash = "two" }
            };
            var repository = RepositoryReturning(userToken);
            // STEP-02: Declare auditEntry to capture a call argument; mock IPasswordService and 1 more; stub repository.GetActiveSessionsAsync and 2 more.
            repository.Setup(x => x.GetActiveSessionsAsync(userToken.UserId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(sessions);
            var passwordService = new Mock<IPasswordService>();
            passwordService.Setup(x => x.Hash("Password2")).Returns("new-password-hash");
            var auditLogService = new Mock<IAuditLogService>();
            AuditLogEntry? auditEntry = null;
            auditLogService.Setup(x => x.Add(It.IsAny<AuditLogEntry>()))
                .Callback<AuditLogEntry>(entry => auditEntry = entry);
            // STEP-03: Create unitOfWork through CreateUnitOfWork.
            var unitOfWork = CreateUnitOfWork(out var transaction);
            // STEP-04: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, unitOfWork, passwordService, auditLogService);

            // STEP-05: Run useCase.ExecuteAsync.
            await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-06: Assert userToken.UsedAt is Now and userToken.User.PasswordHash is a test value.
            userToken.UsedAt.Should().Be(Now);
            userToken.User.PasswordHash.Should().Be("new-password-hash");
            // STEP-07: Assert sessions only holds items matching the condition, auditEntry is not null, auditEntry.Action is AuditActions.PasswordReset and 7 more.
            sessions.Should().OnlyContain(session => session.RevokedAt == Now);
            auditEntry.Should().NotBeNull();
            auditEntry!.Action.Should().Be(AuditActions.PasswordReset);
            auditEntry.EntityType.Should().Be(AuditEntityTypes.User);
            auditEntry.EntityId.Should().Be(userToken.UserId);
            auditEntry.SubjectUserId.Should().Be(userToken.UserId);
            auditEntry.ActorId.Should().Be(userToken.UserId);
            auditEntry.ActorRole.Should().Be("TRAVELER");
            auditEntry.OldValues.Should().BeNull("password hashes must never be written to audit values");
            auditEntry.NewValues.Should().BeNull("password hashes must never be written to audit values");
            // STEP-08: Verify SaveChangesAsync is called once and CommitAsync is called once.
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
            transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-02: Reject a token hash that does not exist.
        /// <summary>Verifies an unknown AUTH-09 token returns TOKEN_INVALID without persistence.</summary>
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

        // TEST-03: Reject a token issued for email verification.
        /// <summary>Verifies AUTH-09 checks token purpose before mutating account state.</summary>
        [Fact]
        public async Task ExecuteAsync_WithEmailVerificationToken_ShouldThrowTokenInvalid()
        {
            // STEP-01: Create userToken through CreateToken, set userToken.Purpose to UserTokenPurpose.EmailVerify and create unitOfWork through CreateUnitOfWork.
            var userToken = CreateToken();
            userToken.Purpose = UserTokenPurpose.EmailVerify;
            var unitOfWork = CreateUnitOfWork(out _);
            // STEP-02: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(RepositoryReturning(userToken), unitOfWork);

            // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-04: Assert act throws BadRequestException, the thrown exception.ErrorCode is "TOKEN_INVALID" and userToken.User.PasswordHash is a test value.
            (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("TOKEN_INVALID");
            userToken.User.PasswordHash.Should().Be("old-password-hash");
            // STEP-05: Verify SaveChangesAsync is never called.
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-04: Reject an already consumed reset token with the only documented invalid-token error.
        /// <summary>Verifies used AUTH-09 tokens map to TOKEN_INVALID rather than an undocumented conflict.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUsedToken_ShouldThrowTokenInvalid()
        {
            // STEP-01: Create userToken through CreateToken, set userToken.UsedAt to Now.AddMinutes() and create unitOfWork through CreateUnitOfWork.
            var userToken = CreateToken();
            userToken.UsedAt = Now.AddMinutes(-1);
            var unitOfWork = CreateUnitOfWork(out _);
            // STEP-02: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(RepositoryReturning(userToken), unitOfWork);

            // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-04: Assert act throws BadRequestException and the thrown exception.ErrorCode is "TOKEN_INVALID".
            (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("TOKEN_INVALID");
            // STEP-05: Verify SaveChangesAsync is never called.
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-05: Reject a reset token exactly at its expiry instant.
        /// <summary>Verifies the AUTH-09 expiration boundary returns TOKEN_EXPIRED.</summary>
        [Fact]
        public async Task ExecuteAsync_WithTokenAtExpiryBoundary_ShouldThrowTokenExpired()
        {
            // STEP-01: Create userToken through CreateToken, set userToken.ExpiresAt to Now and create unitOfWork through CreateUnitOfWork.
            var userToken = CreateToken();
            userToken.ExpiresAt = Now;
            var unitOfWork = CreateUnitOfWork(out _);
            // STEP-02: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(RepositoryReturning(userToken), unitOfWork);

            // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-04: Assert act throws BadRequestException and the thrown exception.ErrorCode is "TOKEN_EXPIRED".
            (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("TOKEN_EXPIRED");
            // STEP-05: Verify SaveChangesAsync is never called.
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-06: Accept a reset token with one tick of validity remaining and no active sessions.
        /// <summary>Verifies the last valid AUTH-09 instant succeeds even when there is no session to revoke.</summary>
        [Fact]
        public async Task ExecuteAsync_WithOneTickRemainingAndNoSessions_ShouldResetPassword()
        {
            // STEP-01: Create userToken through CreateToken, set userToken.ExpiresAt to Now.AddTicks() and get repository from RepositoryReturning.
            var userToken = CreateToken();
            userToken.ExpiresAt = Now.AddTicks(1);
            var repository = RepositoryReturning(userToken);
            // STEP-02: Mock IPasswordService; stub repository.GetActiveSessionsAsync and passwordService.Hash.
            repository.Setup(x => x.GetActiveSessionsAsync(userToken.UserId, It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var passwordService = new Mock<IPasswordService>();
            passwordService.Setup(x => x.Hash("Password2")).Returns("new-password-hash");
            // STEP-03: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, CreateUnitOfWork(out _), passwordService);

            // STEP-04: Run useCase.ExecuteAsync.
            await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-05: Assert userToken.UsedAt is Now and userToken.User.PasswordHash is a test value.
            userToken.UsedAt.Should().Be(Now);
            userToken.User.PasswordHash.Should().Be("new-password-hash");
        }

        // TEST-07: Validate malformed input before hashing a token or opening a transaction.
        /// <summary>Verifies FluentValidation is the first AUTH-09 operation.</summary>
        [Fact]
        public async Task ExecuteAsync_WithInvalidRequest_ShouldValidateBeforeSecurityOrDatabaseAccess()
        {
            // STEP-01: Mock IAuthenticationRepository, IUnitOfWork and IAuthenticationTokenService.
            var repository = new Mock<IAuthenticationRepository>();
            var unitOfWork = new Mock<IUnitOfWork>();
            var tokenService = new Mock<IAuthenticationTokenService>();
            // STEP-02: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(repository, unitOfWork, tokenService: tokenService);

            // STEP-03: Wrap useCase.ExecuteAsync with an empty ResetPasswordRequestDto in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(new ResetPasswordRequestDto(), CancellationToken.None);

            // STEP-04: Assert act throws ValidationException.
            await act.Should().ThrowAsync<ValidationException>();
            // STEP-05: Verify HashUserToken is never called and BeginTransactionAsync is never called.
            tokenService.Verify(x => x.HashUserToken(It.IsAny<string>()), Times.Never);
            unitOfWork.Verify(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-08: Prefer TOKEN_INVALID when a consumed token is also expired.
        /// <summary>Verifies AUTH-09 does not reveal expiration details for an already used token.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUsedAndExpiredToken_ShouldThrowTokenInvalid()
        {
            // STEP-01: Create userToken through CreateToken, set userToken.UsedAt to Now.AddHours() and set userToken.ExpiresAt to Now.AddHours().
            var userToken = CreateToken();
            userToken.UsedAt = Now.AddHours(-2);
            userToken.ExpiresAt = Now.AddHours(-1);
            // STEP-02: Build useCase under test through CreateUseCase.
            var useCase = CreateUseCase(RepositoryReturning(userToken), CreateUnitOfWork(out _));

            // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            // STEP-04: Assert act throws BadRequestException and the thrown exception.ErrorCode is "TOKEN_INVALID".
            (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("TOKEN_INVALID");
        }

        /// <summary>Creates the use case with deterministic dependencies, token hashing, and time.</summary>
        private static ResetPasswordUseCase CreateUseCase(
            Mock<IAuthenticationRepository> repository,
            Mock<IUnitOfWork> unitOfWork,
            Mock<IPasswordService>? passwordService = null,
            Mock<IAuditLogService>? auditLogService = null,
            Mock<IAuthenticationTokenService>? tokenService = null)
        {
            tokenService ??= new Mock<IAuthenticationTokenService>();
            tokenService.Setup(x => x.HashUserToken("raw-token")).Returns("token-hash");
            return new ResetPasswordUseCase(
                new ResetPasswordRequestDtoValidator(),
                repository.Object,
                tokenService.Object,
                (passwordService ?? new Mock<IPasswordService>()).Object,
                (auditLogService ?? new Mock<IAuditLogService>()).Object,
                unitOfWork.Object,
                new FixedTimeProvider(Now));
        }

        /// <summary>Creates a unit of work whose transaction and save calls can be asserted.</summary>
        private static Mock<IUnitOfWork> CreateUnitOfWork(out Mock<IUnitOfWorkTransaction> transaction)
        {
            transaction = new Mock<IUnitOfWorkTransaction>();
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(transaction.Object);
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(4);
            return unitOfWork;
        }

        /// <summary>Creates a repository that returns the supplied token and no active sessions by default.</summary>
        private static Mock<IAuthenticationRepository> RepositoryReturning(UserToken token)
        {
            var repository = new Mock<IAuthenticationRepository>();
            repository.Setup(x => x.GetUserTokenByHashForUpdateAsync("token-hash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(token);
            repository.Setup(x => x.GetActiveSessionsAsync(token.UserId, It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            return repository;
        }

        /// <summary>Creates a valid password-reset token and the fully loaded owning user.</summary>
        private static UserToken CreateToken()
        {
            return new UserToken
            {
                Id = 1,
                UserId = 12,
                Purpose = UserTokenPurpose.PasswordReset,
                TokenHash = "token-hash",
                ExpiresAt = Now.AddMinutes(1),
                User = new User
                {
                    Id = 12,
                    Email = "user@angi.test",
                    DisplayName = "Test User",
                    PasswordHash = "old-password-hash",
                    Role = new Role { Id = 1, Code = "TRAVELER", Name = "Traveler" }
                }
            };
        }

        /// <summary>Creates a valid request whose raw token maps to the repository token hash.</summary>
        private static ResetPasswordRequestDto ValidRequest() => new()
        {
            Token = "raw-token",
            NewPassword = "Password2"
        };

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
