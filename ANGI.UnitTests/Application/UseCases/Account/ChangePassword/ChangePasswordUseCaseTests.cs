using System.Net;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Account;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Account;
using ANGI.Application.UseCases.Account.ChangePassword;
using ANGI.Application.UseCases.Validators.Account;
using ANGI.Domain.Entities;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Account.ChangePassword;

public sealed class ChangePasswordUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private const int TestUserId = 42;
    private const string CurrentUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)";
    private static readonly IPAddress CurrentIp = IPAddress.Parse("127.0.0.1");

    // TEST-01: Happy Path - Change password and revoke other sessions, keeping the session named by the sid claim even
    // when another session has the same User-Agent and is newer.
    [Fact]
    public async Task ExecuteAsync_WithValidCurrentAndNewPassword_ShouldUpdatePasswordHashAndRevokeOtherSessions()
    {
        // Arrange
        var user = new User
        {
            Id = TestUserId,
            Email = "user@example.com",
            PasswordHash = "old_hashed_password"
        };

        var currentSession = new UserSession
        {
            Id = 1,
            UserId = TestUserId,
            UserAgent = CurrentUserAgent,
            IpAddress = CurrentIp,
            CreatedAt = Now.AddHours(-1),
            RevokedAt = null
        };

        var otherSession = new UserSession
        {
            Id = 2,
            UserId = TestUserId,
            UserAgent = CurrentUserAgent,
            IpAddress = IPAddress.Parse("192.168.1.100"),
            CreatedAt = Now.AddMinutes(-5),
            RevokedAt = null
        };

        var accountRepository = new Mock<IAccountRepository>();
        accountRepository.Setup(r => r.GetUserByIdAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        accountRepository.Setup(r => r.GetActiveSessionsAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserSession> { otherSession, currentSession });

        var passwordService = new Mock<IPasswordService>();
        passwordService.Setup(p => p.Verify("OldPassword123", "old_hashed_password")).Returns(true);
        passwordService.Setup(p => p.Verify("NewPassword456", "old_hashed_password")).Returns(false);
        passwordService.Setup(p => p.Hash("NewPassword456")).Returns("new_hashed_password");

        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService.SetupGet(u => u.UserId).Returns(TestUserId);
        currentUserService.SetupGet(u => u.SessionId).Returns(currentSession.Id);
        currentUserService.SetupGet(u => u.UserAgent).Returns(CurrentUserAgent);
        currentUserService.SetupGet(u => u.IpAddress).Returns(CurrentIp);

        var auditLogService = new Mock<IAuditLogService>();
        var unitOfWork = new Mock<IUnitOfWork>();

        var useCase = new ChangePasswordUseCase(
            new ChangePasswordRequestDtoValidator(),
            accountRepository.Object,
            passwordService.Object,
            currentUserService.Object,
            auditLogService.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));

        var request = new ChangePasswordRequestDto
        {
            CurrentPassword = "OldPassword123",
            NewPassword = "NewPassword456"
        };

        // Act
        await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        user.PasswordHash.Should().Be("new_hashed_password");
        currentSession.RevokedAt.Should().BeNull("current session should remain active");
        otherSession.RevokedAt.Should().Be(Now, "other sessions must be revoked");

        auditLogService.Verify(a => a.Add(It.Is<AuditLogEntry>(e =>
            e.Action == AuditActions.PasswordChanged &&
            e.EntityType == AuditEntityTypes.User &&
            e.EntityId == TestUserId &&
            e.SubjectUserId == TestUserId)), Times.Once);

        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Exception Path - Throws WRONG_CURRENT_PASSWORD when oldPassword does not match.
    [Fact]
    public async Task ExecuteAsync_WithIncorrectOldPassword_ShouldThrowBadRequestException()
    {
        // Arrange
        var user = new User
        {
            Id = TestUserId,
            Email = "user@example.com",
            PasswordHash = "stored_hash"
        };

        var accountRepository = new Mock<IAccountRepository>();
        accountRepository.Setup(r => r.GetUserByIdAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var passwordService = new Mock<IPasswordService>();
        passwordService.Setup(p => p.Verify("WrongPassword123", "stored_hash")).Returns(false);

        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService.SetupGet(u => u.UserId).Returns(TestUserId);

        var auditLogService = new Mock<IAuditLogService>();
        var unitOfWork = new Mock<IUnitOfWork>();

        var useCase = new ChangePasswordUseCase(
            new ChangePasswordRequestDtoValidator(),
            accountRepository.Object,
            passwordService.Object,
            currentUserService.Object,
            auditLogService.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));

        var request = new ChangePasswordRequestDto
        {
            OldPassword = "WrongPassword123",
            NewPassword = "NewPassword456"
        };

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<BadRequestException>();
        ex.Which.ErrorCode.Should().Be("WRONG_CURRENT_PASSWORD");

        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        auditLogService.Verify(a => a.Add(It.IsAny<AuditLogEntry>()), Times.Never);
    }

    // TEST-03: Exception Path - Throws WRONG_CURRENT_PASSWORD when user has password but request omits it.
    [Fact]
    public async Task ExecuteAsync_WhenUserHasPasswordButRequestOmitsIt_ShouldThrowBadRequestException()
    {
        // Arrange
        var user = new User
        {
            Id = TestUserId,
            PasswordHash = "existing_hash"
        };

        var accountRepository = new Mock<IAccountRepository>();
        accountRepository.Setup(r => r.GetUserByIdAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var passwordService = new Mock<IPasswordService>();
        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService.SetupGet(u => u.UserId).Returns(TestUserId);

        var useCase = new ChangePasswordUseCase(
            new ChangePasswordRequestDtoValidator(),
            accountRepository.Object,
            passwordService.Object,
            currentUserService.Object,
            Mock.Of<IAuditLogService>(),
            Mock.Of<IUnitOfWork>(),
            new FixedTimeProvider(Now));

        var request = new ChangePasswordRequestDto
        {
            NewPassword = "NewPassword456"
        };

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<BadRequestException>();
        ex.Which.ErrorCode.Should().Be("WRONG_CURRENT_PASSWORD");
    }

    // TEST-04: Happy Path - Google-only user setting password for the first time without current password.
    [Fact]
    public async Task ExecuteAsync_ForGoogleUserWithoutExistingPassword_ShouldSetPasswordSuccessfully()
    {
        // Arrange
        var user = new User
        {
            Id = TestUserId,
            PasswordHash = null // Google user
        };

        var session = new UserSession
        {
            Id = 1,
            UserId = TestUserId,
            CreatedAt = Now,
            RevokedAt = null
        };

        var accountRepository = new Mock<IAccountRepository>();
        accountRepository.Setup(r => r.GetUserByIdAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        accountRepository.Setup(r => r.GetActiveSessionsAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserSession> { session });

        var passwordService = new Mock<IPasswordService>();
        passwordService.Setup(p => p.Hash("NewPassword456")).Returns("google_user_new_hash");

        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService.SetupGet(u => u.UserId).Returns(TestUserId);
        currentUserService.SetupGet(u => u.SessionId).Returns(session.Id);

        var auditLogService = new Mock<IAuditLogService>();
        var unitOfWork = new Mock<IUnitOfWork>();

        var useCase = new ChangePasswordUseCase(
            new ChangePasswordRequestDtoValidator(),
            accountRepository.Object,
            passwordService.Object,
            currentUserService.Object,
            auditLogService.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));

        var request = new ChangePasswordRequestDto
        {
            NewPassword = "NewPassword456"
        };

        // Act
        await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        user.PasswordHash.Should().Be("google_user_new_hash");
        session.RevokedAt.Should().BeNull();
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-05: Exception Path - Throws ValidationException when newPassword matches currentPassword in DTO.
    [Fact]
    public async Task ExecuteAsync_WhenNewPasswordEqualsCurrentPassword_ShouldThrowValidationException()
    {
        // Arrange
        var useCase = new ChangePasswordUseCase(
            new ChangePasswordRequestDtoValidator(),
            Mock.Of<IAccountRepository>(),
            Mock.Of<IPasswordService>(),
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IAuditLogService>(),
            Mock.Of<IUnitOfWork>(),
            new FixedTimeProvider(Now));

        var request = new ChangePasswordRequestDto
        {
            CurrentPassword = "SamePassword123",
            NewPassword = "SamePassword123"
        };

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
    }

    // TEST-06: Exception Path - Throws UNAUTHORIZED when user is not authenticated.
    [Fact]
    public async Task ExecuteAsync_WhenNotAuthenticated_ShouldThrowUnauthorizedException()
    {
        // Arrange
        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService.SetupGet(u => u.UserId).Returns((int?)null);

        var useCase = new ChangePasswordUseCase(
            new ChangePasswordRequestDtoValidator(),
            Mock.Of<IAccountRepository>(),
            Mock.Of<IPasswordService>(),
            currentUserService.Object,
            Mock.Of<IAuditLogService>(),
            Mock.Of<IUnitOfWork>(),
            new FixedTimeProvider(Now));

        var request = new ChangePasswordRequestDto
        {
            CurrentPassword = "OldPassword123",
            NewPassword = "NewPassword456"
        };

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<UnauthorizedException>();
        ex.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    // TEST-07: Revoke every session when the access token has no sid claim, since the current session is unknown.
    [Fact]
    public async Task ExecuteAsync_WhenTokenHasNoSessionId_ShouldRevokeAllSessions()
    {
        var user = new User { Id = TestUserId, PasswordHash = null };
        var sessions = new List<UserSession>
        {
            new() { Id = 1, UserId = TestUserId, UserAgent = CurrentUserAgent, CreatedAt = Now },
            new() { Id = 2, UserId = TestUserId, UserAgent = CurrentUserAgent, CreatedAt = Now.AddHours(-1) }
        };
        var accountRepository = new Mock<IAccountRepository>();
        accountRepository.Setup(r => r.GetUserByIdAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        accountRepository.Setup(r => r.GetActiveSessionsAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sessions);
        var passwordService = new Mock<IPasswordService>();
        passwordService.Setup(p => p.Hash("NewPassword456")).Returns("new_hash");
        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService.SetupGet(u => u.UserId).Returns(TestUserId);
        currentUserService.SetupGet(u => u.UserAgent).Returns(CurrentUserAgent);
        var useCase = new ChangePasswordUseCase(
            new ChangePasswordRequestDtoValidator(),
            accountRepository.Object,
            passwordService.Object,
            currentUserService.Object,
            Mock.Of<IAuditLogService>(),
            Mock.Of<IUnitOfWork>(),
            new FixedTimeProvider(Now));

        await useCase.ExecuteAsync(new ChangePasswordRequestDto { NewPassword = "NewPassword456" }, CancellationToken.None);

        sessions.Should().OnlyContain(session => session.RevokedAt == Now);
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
