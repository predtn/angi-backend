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

namespace ANGI.Test.Application.UseCases.Auth.ResetPassword
{
    public sealed class ResetPasswordUseCaseTests
    {
        private static readonly DateTime Now = new(2026, 10, 17, 3, 0, 0, DateTimeKind.Utc);

        // TEST-01: Consume a valid reset token, replace the hash, revoke every active session, and audit the user.
        /// <summary>Verifies all AUTH-09 writes are prepared and committed in one successful transaction.</summary>
        [Fact]
        public async Task ExecuteAsync_WithValidToken_ShouldResetPasswordRevokeSessionsAndWriteAudit()
        {
            var userToken = CreateToken();
            var sessions = new[]
            {
                new UserSession { Id = 10, UserId = userToken.UserId, RefreshTokenHash = "one" },
                new UserSession { Id = 11, UserId = userToken.UserId, RefreshTokenHash = "two" }
            };
            var repository = RepositoryReturning(userToken);
            repository.Setup(x => x.GetActiveSessionsAsync(userToken.UserId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(sessions);
            var passwordService = new Mock<IPasswordService>();
            passwordService.Setup(x => x.Hash("Password2")).Returns("new-password-hash");
            var auditLogService = new Mock<IAuditLogService>();
            AuditLogEntry? auditEntry = null;
            auditLogService.Setup(x => x.Add(It.IsAny<AuditLogEntry>()))
                .Callback<AuditLogEntry>(entry => auditEntry = entry);
            var unitOfWork = CreateUnitOfWork(out var transaction);
            var useCase = CreateUseCase(repository, unitOfWork, passwordService, auditLogService);

            await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            userToken.UsedAt.Should().Be(Now);
            userToken.User.PasswordHash.Should().Be("new-password-hash");
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
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
            transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        // TEST-02: Reject a token hash that does not exist.
        /// <summary>Verifies an unknown AUTH-09 token returns TOKEN_INVALID without persistence.</summary>
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

        // TEST-03: Reject a token issued for email verification.
        /// <summary>Verifies AUTH-09 checks token purpose before mutating account state.</summary>
        [Fact]
        public async Task ExecuteAsync_WithEmailVerificationToken_ShouldThrowTokenInvalid()
        {
            var userToken = CreateToken();
            userToken.Purpose = UserTokenPurpose.EmailVerify;
            var unitOfWork = CreateUnitOfWork(out _);
            var useCase = CreateUseCase(RepositoryReturning(userToken), unitOfWork);

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("TOKEN_INVALID");
            userToken.User.PasswordHash.Should().Be("old-password-hash");
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-04: Reject an already consumed reset token with the only documented invalid-token error.
        /// <summary>Verifies used AUTH-09 tokens map to TOKEN_INVALID rather than an undocumented conflict.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUsedToken_ShouldThrowTokenInvalid()
        {
            var userToken = CreateToken();
            userToken.UsedAt = Now.AddMinutes(-1);
            var unitOfWork = CreateUnitOfWork(out _);
            var useCase = CreateUseCase(RepositoryReturning(userToken), unitOfWork);

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("TOKEN_INVALID");
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-05: Reject a reset token exactly at its expiry instant.
        /// <summary>Verifies the AUTH-09 expiration boundary returns TOKEN_EXPIRED.</summary>
        [Fact]
        public async Task ExecuteAsync_WithTokenAtExpiryBoundary_ShouldThrowTokenExpired()
        {
            var userToken = CreateToken();
            userToken.ExpiresAt = Now;
            var unitOfWork = CreateUnitOfWork(out _);
            var useCase = CreateUseCase(RepositoryReturning(userToken), unitOfWork);

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("TOKEN_EXPIRED");
            unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-06: Accept a reset token with one tick of validity remaining and no active sessions.
        /// <summary>Verifies the last valid AUTH-09 instant succeeds even when there is no session to revoke.</summary>
        [Fact]
        public async Task ExecuteAsync_WithOneTickRemainingAndNoSessions_ShouldResetPassword()
        {
            var userToken = CreateToken();
            userToken.ExpiresAt = Now.AddTicks(1);
            var repository = RepositoryReturning(userToken);
            repository.Setup(x => x.GetActiveSessionsAsync(userToken.UserId, It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var passwordService = new Mock<IPasswordService>();
            passwordService.Setup(x => x.Hash("Password2")).Returns("new-password-hash");
            var useCase = CreateUseCase(repository, CreateUnitOfWork(out _), passwordService);

            await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

            userToken.UsedAt.Should().Be(Now);
            userToken.User.PasswordHash.Should().Be("new-password-hash");
        }

        // TEST-07: Validate malformed input before hashing a token or opening a transaction.
        /// <summary>Verifies FluentValidation is the first AUTH-09 operation.</summary>
        [Fact]
        public async Task ExecuteAsync_WithInvalidRequest_ShouldValidateBeforeSecurityOrDatabaseAccess()
        {
            var repository = new Mock<IAuthenticationRepository>();
            var unitOfWork = new Mock<IUnitOfWork>();
            var tokenService = new Mock<IAuthenticationTokenService>();
            var useCase = CreateUseCase(repository, unitOfWork, tokenService: tokenService);

            var act = () => useCase.ExecuteAsync(new ResetPasswordRequestDto(), CancellationToken.None);

            await act.Should().ThrowAsync<ValidationException>();
            tokenService.Verify(x => x.HashUserToken(It.IsAny<string>()), Times.Never);
            unitOfWork.Verify(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // TEST-08: Prefer TOKEN_INVALID when a consumed token is also expired.
        /// <summary>Verifies AUTH-09 does not reveal expiration details for an already used token.</summary>
        [Fact]
        public async Task ExecuteAsync_WithUsedAndExpiredToken_ShouldThrowTokenInvalid()
        {
            var userToken = CreateToken();
            userToken.UsedAt = Now.AddHours(-2);
            userToken.ExpiresAt = Now.AddHours(-1);
            var useCase = CreateUseCase(RepositoryReturning(userToken), CreateUnitOfWork(out _));

            var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

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
