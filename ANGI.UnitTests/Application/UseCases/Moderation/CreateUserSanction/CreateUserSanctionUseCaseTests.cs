using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation;
using ANGI.Application.UseCases.Moderation.CreateUserSanction;
using ANGI.Application.UseCases.Validators.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.CreateUserSanction;

/// <summary>Tests MOD-U3: creating user sanctions, role guards, validation and side effects.</summary>
public sealed class CreateUserSanctionUseCaseTests
{
    private static CreateUserSanctionUseCase CreateUseCase(ModerationTestContext world) =>
        new(
            new CreateUserSanctionRequestDtoValidator(world.Clock),
            world.Access,
            world.UserModerationRepo.Object,
            world.UnitOfWork.Object,
            world.UserEffects,
            world.Cache.Object,
            world.Clock);

    // TEST-01: Happy Path - Suspend user successfully
    [Fact]
    public async Task SuspendUser_HappyPath_UpdatesStatus_RevokesSessions_CreatesAuditAndNotification()
    {
        // Arrange
        var world = new ModerationTestContext();
        var targetUser = new User
        {
            Id = 12,
            Email = "traveler@test.local",
            DisplayName = "Traveler User",
            Role = new Role { Code = "TRAVELER" },
            Status = UserStatus.Active
        };
        var activeSession = new UserSession { Id = 101, UserId = 12 };

        world.UserModerationRepo.Setup(x => x.LockUserAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(targetUser);
        world.UserModerationRepo.Setup(x => x.GetSessionsAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync([activeSession]);
        world.UserModerationRepo.Setup(x => x.GetUserSummaryAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSummaryDto { Id = 3, DisplayName = "Mod Staff" });

        var committed = false;
        world.Transaction.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>()))
            .Callback(() => committed = true)
            .Returns(Task.CompletedTask);

        var request = new CreateUserSanctionRequestDto
        {
            Type = "suspend",
            DurationDays = 7,
            Reason = "Spamming commercial links",
            ReportId = 88
        };

        // Act
        var result = await CreateUseCase(world).ExecuteAsync(12, request, default);

        // Assert
        result.Should().NotBeNull();
        result.Type.Should().Be("suspend");
        result.Reason.Should().Be("Spamming commercial links");
        result.ReportId.Should().Be(88);
        result.IssuedBy.Id.Should().Be(3);
        result.EndsAt.Should().Be(world.Clock.GetUtcNow().AddDays(7).UtcDateTime);

        targetUser.Status.Should().Be(UserStatus.Suspended);
        targetUser.SuspendedUntil.Should().Be(world.Clock.GetUtcNow().AddDays(7).UtcDateTime);
        activeSession.RevokedAt.Should().Be(world.Clock.GetUtcNow().UtcDateTime);

        world.UserModerationRepo.Verify(x => x.AddSanction(It.Is<UserSanction>(s =>
            s.UserId == 12 &&
            s.Type == SanctionType.Suspend &&
            s.Reason == "Spamming commercial links" &&
            s.IssuedBy == 3 &&
            s.ReportId == 88 &&
            s.EndsAt == world.Clock.GetUtcNow().AddDays(7).UtcDateTime)), Times.Once);

        world.Audit.Verify(x => x.Add(It.Is<AuditLogEntry>(a =>
            a.Action == AuditActions.UserSuspended &&
            a.EntityId == 12 &&
            a.SubjectUserId == 12)), Times.Once);

        world.UserModerationRepo.Verify(x => x.AddNotification(It.Is<Domain.Entities.Notification>(n =>
            n.RecipientId == 12 &&
            n.Type == "ACCOUNT_SANCTIONED")), Times.Once);

        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        committed.Should().BeTrue();
        world.Cache.Verify(x => x.Invalidate(12), Times.Once);
    }

    // TEST-02: Happy Path - Ban user successfully
    [Fact]
    public async Task BanUser_HappyPath_UpdatesStatusToBanned_NoEndsAtRequired()
    {
        // Arrange
        var world = new ModerationTestContext();
        var targetUser = new User
        {
            Id = 20,
            Email = "badowner@test.local",
            DisplayName = "Bad Owner",
            Role = new Role { Code = "RESTAURANT_OWNER" },
            Status = UserStatus.Active
        };
        var session = new UserSession { Id = 201, UserId = 20 };

        world.UserModerationRepo.Setup(x => x.LockUserAsync(20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(targetUser);
        world.UserModerationRepo.Setup(x => x.GetSessionsAsync(20, It.IsAny<CancellationToken>()))
            .ReturnsAsync([session]);
        world.UserModerationRepo.Setup(x => x.GetUserSummaryAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSummaryDto { Id = 3, DisplayName = "Mod Staff" });

        var request = new CreateUserSanctionRequestDto
        {
            Type = "ban",
            Reason = "Fraudulent restaurant listings and scams"
        };

        // Act
        var result = await CreateUseCase(world).ExecuteAsync(20, request, default);

        // Assert
        result.Type.Should().Be("ban");
        result.EndsAt.Should().BeNull();
        targetUser.Status.Should().Be(UserStatus.Banned);
        targetUser.SuspendedUntil.Should().BeNull();
        session.RevokedAt.Should().Be(world.Clock.GetUtcNow().UtcDateTime);

        world.Audit.Verify(x => x.Add(It.Is<AuditLogEntry>(a => a.Action == AuditActions.UserBanned)), Times.Once);
        world.Cache.Verify(x => x.Invalidate(20), Times.Once);
    }

    // TEST-03: Happy Path - Warn user successfully
    [Fact]
    public async Task WarnUser_HappyPath_PreservesStatus_DoesNotRevokeSessions()
    {
        // Arrange
        var world = new ModerationTestContext();
        var targetUser = new User
        {
            Id = 25,
            DisplayName = "Warned User",
            Role = new Role { Code = "TRAVELER" },
            Status = UserStatus.Active
        };
        var session = new UserSession { Id = 251, UserId = 25 };

        world.UserModerationRepo.Setup(x => x.LockUserAsync(25, It.IsAny<CancellationToken>()))
            .ReturnsAsync(targetUser);
        world.UserModerationRepo.Setup(x => x.GetSessionsAsync(25, It.IsAny<CancellationToken>()))
            .ReturnsAsync([session]);
        world.UserModerationRepo.Setup(x => x.GetUserSummaryAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSummaryDto { Id = 3, DisplayName = "Mod Staff" });

        var request = new CreateUserSanctionRequestDto
        {
            Type = "warn",
            Reason = "Minor violation warning"
        };

        // Act
        var result = await CreateUseCase(world).ExecuteAsync(25, request, default);

        // Assert
        result.Type.Should().Be("warn");
        targetUser.Status.Should().Be(UserStatus.Active);
        session.RevokedAt.Should().BeNull();
        world.Audit.Verify(x => x.Add(It.Is<AuditLogEntry>(a => a.Action == AuditActions.UserWarned)), Times.Once);
        world.Cache.Verify(x => x.Invalidate(It.IsAny<int>()), Times.Never);
    }

    // TEST-04: Exception Path 1 - 403 Forbidden when Mod attempts to sanction an Admin
    [Fact]
    public async Task SanctionUser_TargetIsAdmin_Throws403CannotSanctionStaff()
    {
        // Arrange
        var world = new ModerationTestContext();
        var adminUser = new User
        {
            Id = 1,
            Email = "admin@angi.vn",
            DisplayName = "Super Admin",
            Role = new Role { Code = "ADMIN" },
            Status = UserStatus.Active
        };

        world.UserModerationRepo.Setup(x => x.LockUserAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(adminUser);

        var request = new CreateUserSanctionRequestDto
        {
            Type = "ban",
            Reason = "Trying to ban the administrator"
        };

        // Act
        var act = () => CreateUseCase(world).ExecuteAsync(1, request, default);

        // Assert
        var exception = await act.Should().ThrowAsync<ForbiddenException>();
        exception.Which.ErrorCode.Should().Be("CANNOT_SANCTION_STAFF");
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: Exception Path 1b - 403 Forbidden when Mod attempts to sanction another Mod
    [Fact]
    public async Task SanctionUser_TargetIsMod_Throws403CannotSanctionStaff()
    {
        // Arrange
        var world = new ModerationTestContext();
        var peerMod = new User
        {
            Id = 5,
            Email = "peer.mod@angi.vn",
            DisplayName = "Peer Mod",
            Role = new Role { Code = "MOD" },
            Status = UserStatus.Active
        };

        world.UserModerationRepo.Setup(x => x.LockUserAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(peerMod);

        var request = new CreateUserSanctionRequestDto
        {
            Type = "suspend",
            DurationDays = 3,
            Reason = "Peer dispute"
        };

        // Act
        var act = () => CreateUseCase(world).ExecuteAsync(5, request, default);

        // Assert
        var exception = await act.Should().ThrowAsync<ForbiddenException>();
        exception.Which.ErrorCode.Should().Be("CANNOT_SANCTION_STAFF");
    }

    // TEST-06: Exception Path 2 - 400 Validation Error when choosing SUSPEND without duration/endsAt
    [Fact]
    public async Task SanctionUser_SuspendWithoutDurationOrEndsAt_ThrowsValidationException()
    {
        // Arrange
        var world = new ModerationTestContext();
        var request = new CreateUserSanctionRequestDto
        {
            Type = "suspend",
            Reason = "No duration specified"
            // EndsAt and DurationDays are null
        };

        // Act
        var act = () => CreateUseCase(world).ExecuteAsync(12, request, default);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
        world.UserModerationRepo.Verify(x => x.LockUserAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-07: Exception Path 2b - 400 Validation Error when endsAt is in the past
    [Fact]
    public async Task SanctionUser_SuspendWithPastEndsAt_ThrowsValidationException()
    {
        // Arrange
        var world = new ModerationTestContext();
        var request = new CreateUserSanctionRequestDto
        {
            Type = "suspend",
            Reason = "Past endsAt",
            EndsAt = world.Clock.GetUtcNow().AddHours(-1)
        };

        // Act
        var act = () => CreateUseCase(world).ExecuteAsync(12, request, default);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
    }

    // TEST-08: Exception Path 2c - 400 Validation Error when reason is empty
    [Fact]
    public async Task SanctionUser_EmptyReason_ThrowsValidationException()
    {
        // Arrange
        var world = new ModerationTestContext();
        var request = new CreateUserSanctionRequestDto
        {
            Type = "ban",
            Reason = ""
        };

        // Act
        var act = () => CreateUseCase(world).ExecuteAsync(12, request, default);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
    }

    // TEST-09: Exception Path 3 - 404 Not Found when userId does not exist
    [Fact]
    public async Task SanctionUser_UserNotFound_Throws404NotFoundException()
    {
        // Arrange
        var world = new ModerationTestContext();
        world.UserModerationRepo.Setup(x => x.LockUserAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var request = new CreateUserSanctionRequestDto
        {
            Type = "ban",
            Reason = "Target does not exist"
        };

        // Act
        var act = () => CreateUseCase(world).ExecuteAsync(999, request, default);

        // Assert
        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    // TEST-10: Exception Path 4 - 409 Conflict when user is already banned
    [Fact]
    public async Task SanctionUser_UserAlreadyBanned_Throws409ConflictException()
    {
        // Arrange
        var world = new ModerationTestContext();
        var bannedUser = new User
        {
            Id = 30,
            Email = "banned@test.local",
            DisplayName = "Already Banned User",
            Role = new Role { Code = "TRAVELER" },
            Status = UserStatus.Banned
        };

        world.UserModerationRepo.Setup(x => x.LockUserAsync(30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(bannedUser);

        var request = new CreateUserSanctionRequestDto
        {
            Type = "ban",
            Reason = "Ban again"
        };

        // Act
        var act = () => CreateUseCase(world).ExecuteAsync(30, request, default);

        // Assert
        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("USER_ALREADY_BANNED");
    }

    // TEST-11: Exception Path 5 - 403 Forbidden when caller lacks USER_BAN permission
    [Fact]
    public async Task SanctionUser_CallerLacksBanPermission_ThrowsForbiddenException()
    {
        // Arrange
        var world = new ModerationTestContext();
        world.Accounts.Setup(x => x.GetEffectivePermissionsAsync(3, "MOD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["USER_SUSPEND"]); // Has USER_SUSPEND but lacks USER_BAN

        var request = new CreateUserSanctionRequestDto
        {
            Type = "ban",
            Reason = "Attempt ban without permission"
        };

        // Act
        var act = () => CreateUseCase(world).ExecuteAsync(12, request, default);

        // Assert
        var exception = await act.Should().ThrowAsync<ForbiddenException>();
        exception.Which.ErrorCode.Should().Be("FORBIDDEN");
    }
}
