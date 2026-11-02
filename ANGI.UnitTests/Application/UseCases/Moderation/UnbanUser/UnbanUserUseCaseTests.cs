using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.UnbanUser;
using ANGI.Application.UseCases.Validators.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.UnbanUser;

/// <summary>Verifies MOD-U4 unban logic, status transitions, sanction lifting, and error handling.</summary>
public sealed class UnbanUserUseCaseTests
{
    private static UnbanUserUseCase Create(ModerationTestContext world) => new(
        new UnbanUserRequestDtoValidator(),
        world.Access,
        world.UserModerationRepository.Object,
        world.UnitOfWork.Object,
        world.Audit.Object,
        world.Cache.Object,
        world.Clock);

    private static (User User, UserSanction Sanction) SetupBannedUser(int userId = 12)
    {
        var user = new User
        {
            Id = userId,
            Email = "user@test.local",
            DisplayName = "Test User",
            Role = new Role { Code = "TRAVELER" },
            Status = UserStatus.Banned,
            SuspendedUntil = null,
            CreatedAt = new DateTime(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc)
        };
        var sanction = new UserSanction
        {
            Id = 101,
            UserId = userId,
            Type = SanctionType.Ban,
            Reason = "Vi phạm tiêu chuẩn cộng đồng",
            IssuedBy = 3,
            StartsAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            EndsAt = null,
            LiftedAt = null,
            LiftedBy = null,
            LiftReason = null
        };
        return (user, sanction);
    }

    // TEST-01: Happy Path - Successfully unbans a banned user
    /// <summary>Verifies that unbanning restores status to Active, updates sanction, creates audit and notification, and commits.</summary>
    [Fact]
    public async Task ExecuteAsync_HappyPath_BannedUser_RestoresActiveStatus()
    {
        // Arrange
        var world = new ModerationTestContext();
        var (user, sanction) = SetupBannedUser(12);

        world.UserModerationRepository
            .Setup(x => x.LockUserAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        world.UserModerationRepository
            .Setup(x => x.GetActiveSanctionsAsync(12, world.Clock.GetUtcNow().UtcDateTime, It.IsAny<CancellationToken>()))
            .ReturnsAsync([sanction]);

        var committed = false;
        world.Transaction
            .Setup(x => x.CommitAsync(It.IsAny<CancellationToken>()))
            .Callback(() => committed = true)
            .Returns(Task.CompletedTask);

        world.Cache
            .Setup(x => x.Invalidate(12))
            .Callback(() => committed.Should().BeTrue());

        var request = new UnbanUserRequestDto { LiftReason = "Đã xác minh nhầm tài khoản" };

        // Act
        var result = await Create(world).ExecuteAsync(12, request, default);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(12);
        result.Status.Should().Be("active");
        result.SuspendedUntil.Should().BeNull();

        user.Status.Should().Be(UserStatus.Active);
        user.SuspendedUntil.Should().BeNull();

        sanction.LiftedBy.Should().Be(3);
        sanction.LiftedAt.Should().Be(world.Clock.GetUtcNow().UtcDateTime);
        sanction.LiftReason.Should().Be("Đã xác minh nhầm tài khoản");

        world.UserModerationRepository.Verify(x => x.AddNotification(It.Is<Domain.Entities.Notification>(n =>
            n.RecipientId == 12 &&
            n.Type == "ACCOUNT_UNBANNED" &&
            n.Body == "Đã xác minh nhầm tài khoản")), Times.Once);

        world.Audit.Verify(x => x.Add(It.Is<AuditLogEntry>(a =>
            a.Action == AuditActions.UserUnbanned &&
            a.EntityType == AuditEntityTypes.User &&
            a.EntityId == 12 &&
            a.SubjectUserId == 12)), Times.Once);

        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        world.Cache.Verify(x => x.Invalidate(12), Times.Once);
    }

    // TEST-02: Happy Path - Successfully unbans a suspended user
    /// <summary>Verifies that unbanning a suspended user clears SuspendedUntil and restores status to Active.</summary>
    [Fact]
    public async Task ExecuteAsync_HappyPath_SuspendedUser_ClearsSuspensionDeadline()
    {
        // Arrange
        var world = new ModerationTestContext();
        var user = new User
        {
            Id = 15,
            Email = "suspended@test.local",
            DisplayName = "Suspended User",
            Role = new Role { Code = "TRAVELER" },
            Status = UserStatus.Suspended,
            SuspendedUntil = world.Clock.GetUtcNow().UtcDateTime.AddDays(7),
            CreatedAt = new DateTime(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc)
        };
        var sanction = new UserSanction
        {
            Id = 102,
            UserId = 15,
            Type = SanctionType.Suspend,
            Reason = "Tạm khóa do báo cáo spam",
            IssuedBy = 3,
            StartsAt = world.Clock.GetUtcNow().UtcDateTime.AddDays(-1),
            EndsAt = world.Clock.GetUtcNow().UtcDateTime.AddDays(7)
        };

        world.UserModerationRepository
            .Setup(x => x.LockUserAsync(15, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        world.UserModerationRepository
            .Setup(x => x.GetActiveSanctionsAsync(15, world.Clock.GetUtcNow().UtcDateTime, It.IsAny<CancellationToken>()))
            .ReturnsAsync([sanction]);

        var request = new UnbanUserRequestDto { LiftReason = "Người dùng đã giải trình thỏa đáng" };

        // Act
        var result = await Create(world).ExecuteAsync(15, request, default);

        // Assert
        result.Status.Should().Be("active");
        result.SuspendedUntil.Should().BeNull();
        user.Status.Should().Be(UserStatus.Active);
        user.SuspendedUntil.Should().BeNull();
        sanction.LiftedBy.Should().Be(3);
        sanction.LiftedAt.Should().Be(world.Clock.GetUtcNow().UtcDateTime);
        sanction.LiftReason.Should().Be("Người dùng đã giải trình thỏa đáng");
    }

    // TEST-03: Exception Path 1 - User is already ACTIVE (409 Conflict)
    /// <summary>Verifies 409 Conflict with USER_NOT_SANCTIONED when target user has normal ACTIVE status.</summary>
    [Fact]
    public async Task ExecuteAsync_UserIsActive_ThrowsConflictException()
    {
        // Arrange
        var world = new ModerationTestContext();
        var user = new User
        {
            Id = 20,
            Email = "active@test.local",
            DisplayName = "Active User",
            Role = new Role { Code = "TRAVELER" },
            Status = UserStatus.Active
        };

        world.UserModerationRepository
            .Setup(x => x.LockUserAsync(20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var request = new UnbanUserRequestDto { LiftReason = "Lý do gỡ" };

        // Act
        var act = () => Create(world).ExecuteAsync(20, request, default);

        // Assert
        var ex = await act.Should().ThrowAsync<ConflictException>();
        ex.Which.ErrorCode.Should().Be("USER_NOT_SANCTIONED");

        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.Cache.VerifyNoOtherCalls();
    }

    // TEST-04: Exception Path 2 - User does not exist (404 Not Found)
    /// <summary>Verifies 404 Not Found with USER_NOT_FOUND when user ID does not exist.</summary>
    [Fact]
    public async Task ExecuteAsync_UserNotFound_ThrowsNotFoundException()
    {
        // Arrange
        var world = new ModerationTestContext();
        world.UserModerationRepository
            .Setup(x => x.LockUserAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var request = new UnbanUserRequestDto { LiftReason = "Lý do gỡ" };

        // Act
        var act = () => Create(world).ExecuteAsync(999, request, default);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("USER_NOT_FOUND");

        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.Cache.VerifyNoOtherCalls();
    }

    // TEST-05: Exception Path 3 - User is Banned/Suspended but has no active sanction in DB (409 Conflict)
    /// <summary>Verifies 409 Conflict when user status is Banned but no active sanction records exist.</summary>
    [Fact]
    public async Task ExecuteAsync_NoActiveSanctionFound_ThrowsConflictException()
    {
        // Arrange
        var world = new ModerationTestContext();
        var (user, _) = SetupBannedUser(12);

        world.UserModerationRepository
            .Setup(x => x.LockUserAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        world.UserModerationRepository
            .Setup(x => x.GetActiveSanctionsAsync(12, world.Clock.GetUtcNow().UtcDateTime, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var request = new UnbanUserRequestDto { LiftReason = "Lý do gỡ" };

        // Act
        var act = () => Create(world).ExecuteAsync(12, request, default);

        // Assert
        var ex = await act.Should().ThrowAsync<ConflictException>();
        ex.Which.ErrorCode.Should().Be("USER_NOT_SANCTIONED");

        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.Cache.VerifyNoOtherCalls();
    }

    // TEST-06: Exception Path 4 - Attempting to operate on staff account (403 Forbidden)
    /// <summary>Verifies 403 Forbidden with CANNOT_SANCTION_STAFF when target is Mod or Admin.</summary>
    [Theory]
    [InlineData("MOD")]
    [InlineData("ADMIN")]
    public async Task ExecuteAsync_TargetIsStaff_ThrowsForbiddenException(string roleCode)
    {
        // Arrange
        var world = new ModerationTestContext();
        var staffUser = new User
        {
            Id = 50,
            Email = "staff@test.local",
            DisplayName = "Staff User",
            Role = new Role { Code = roleCode },
            Status = UserStatus.Banned
        };

        world.UserModerationRepository
            .Setup(x => x.LockUserAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(staffUser);

        var request = new UnbanUserRequestDto { LiftReason = "Lý do gỡ" };

        // Act
        var act = () => Create(world).ExecuteAsync(50, request, default);

        // Assert
        var ex = await act.Should().ThrowAsync<ForbiddenException>();
        ex.Which.ErrorCode.Should().Be("CANNOT_SANCTION_STAFF");
    }

    // TEST-07: Exception Path 5 - Mod lacks USER_BAN permission (403 Forbidden)
    /// <summary>Verifies 403 Forbidden when caller Mod lacks the USER_BAN permission.</summary>
    [Fact]
    public async Task ExecuteAsync_ModLacksPermission_ThrowsForbiddenException()
    {
        // Arrange
        var world = new ModerationTestContext();
        world.Accounts
            .Setup(x => x.GetEffectivePermissionsAsync(3, "MOD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["RESTAURANT_MODERATE"]); // missing USER_BAN

        var request = new UnbanUserRequestDto { LiftReason = "Lý do gỡ" };

        // Act
        var act = () => Create(world).ExecuteAsync(12, request, default);

        // Assert
        var ex = await act.Should().ThrowAsync<ForbiddenException>();
        ex.Which.ErrorCode.Should().Be("FORBIDDEN");
    }

    // TEST-08: Exception Path 6 - Validation failure
    /// <summary>Verifies FluentValidation throws ValidationException when request body is invalid.</summary>
    [Fact]
    public async Task ExecuteAsync_EmptyLiftReason_ThrowsValidationException()
    {
        // Arrange
        var world = new ModerationTestContext();
        var request = new UnbanUserRequestDto { LiftReason = "" };

        // Act
        var act = () => Create(world).ExecuteAsync(12, request, default);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
    }
}
