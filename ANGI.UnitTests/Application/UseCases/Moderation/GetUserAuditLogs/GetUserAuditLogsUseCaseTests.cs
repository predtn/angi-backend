using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.GetUserAuditLogs;
using ANGI.Application.UseCases.Validators.Moderation;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.GetUserAuditLogs;

/// <summary>Verifies MOD-U5 (F-55) user audit log retrieval, pagination, date/action filtering, and error handling.</summary>
public sealed class GetUserAuditLogsUseCaseTests
{
    // TEST-01: Happy path returns paginated audit logs filtered by date range and action.
    /// <summary>Ensures MOD-U5 returns the paginated audit log list when the user exists and filters are valid.</summary>
    [Fact]
    public async Task ExecuteAsync_HappyPath_WithPaginationAndDateFilter_ReturnsPagedAuditLogs()
    {
        // Arrange
        var world = new ModerationTestContext();
        var repository = new Mock<IUserModerationRepository>();
        var fromDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var toDate = new DateTime(2026, 10, 5, 23, 59, 59, DateTimeKind.Utc);

        var request = new GetUserAuditLogsRequestDto
        {
            Action = AuditActions.UserSuspended,
            From = fromDate,
            To = toDate,
            Page = 1,
            PageSize = 20
        };

        var expectedPage = new Paged<AuditLogDto>
        {
            Items =
            [
                new AuditLogDto
                {
                    Id = 9921,
                    Actor = new AuditActorDto
                    {
                        Id = 3,
                        DisplayName = "Mod Linh",
                        Role = "MOD"
                    },
                    Action = AuditActions.UserSuspended,
                    EntityType = AuditEntityTypes.User,
                    EntityId = 12,
                    SubjectUser = new UserSummaryDto
                    {
                        Id = 12,
                        DisplayName = "Nguyễn An",
                        AvatarUrl = "https://cdn.angi.vn/avatars/12.jpg"
                    },
                    CreatedAt = new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc)
                }
            ],
            Page = 1,
            PageSize = 20,
            TotalItems = 1,
            TotalPages = 1
        };

        repository.Setup(x => x.UserExistsAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        repository.Setup(x => x.GetUserAuditLogsAsync(12, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPage);

        var useCase = new GetUserAuditLogsUseCase(
            new GetUserAuditLogsRequestDtoValidator(),
            world.Access,
            repository.Object);

        // Act
        var result = await useCase.ExecuteAsync(12, request, default);

        // Assert
        result.Should().NotBeNull();
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
        result.TotalItems.Should().Be(1);
        result.TotalPages.Should().Be(1);
        result.Items.Should().ContainSingle();
        result.Items[0].Id.Should().Be(9921);
        result.Items[0].Action.Should().Be(AuditActions.UserSuspended);
        result.Items[0].Actor.Should().NotBeNull();
        result.Items[0].Actor!.Id.Should().Be(3);
        result.Items[0].Actor!.Role.Should().Be("MOD");
        result.Items[0].SubjectUser.Should().NotBeNull();
        result.Items[0].SubjectUser!.Id.Should().Be(12);
        result.Items[0].CreatedAt.Should().Be(new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc));

        repository.Verify(x => x.UserExistsAsync(12, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(x => x.GetUserAuditLogsAsync(12, request, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Exception path throws 404 USER_NOT_FOUND when userId does not exist.
    /// <summary>Ensures a non-existent userId throws NotFoundException with USER_NOT_FOUND.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenUserDoesNotExist_ThrowsNotFoundException()
    {
        // Arrange
        var world = new ModerationTestContext();
        var repository = new Mock<IUserModerationRepository>();
        var request = new GetUserAuditLogsRequestDto { Page = 1, PageSize = 20 };

        repository.Setup(x => x.UserExistsAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var useCase = new GetUserAuditLogsUseCase(
            new GetUserAuditLogsRequestDtoValidator(),
            world.Access,
            repository.Object);

        // Act
        var act = () => useCase.ExecuteAsync(999, request, default);

        // Assert
        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("USER_NOT_FOUND");
        repository.Verify(x => x.GetUserAuditLogsAsync(It.IsAny<int>(), It.IsAny<GetUserAuditLogsRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Non-positive userId throws 404 USER_NOT_FOUND without querying the repository.
    /// <summary>Prevents invalid non-positive userId values from hitting the database.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenUserIdIsNonPositive_ThrowsNotFoundException()
    {
        // Arrange
        var world = new ModerationTestContext();
        var repository = new Mock<IUserModerationRepository>();
        var request = new GetUserAuditLogsRequestDto { Page = 1, PageSize = 20 };

        var useCase = new GetUserAuditLogsUseCase(
            new GetUserAuditLogsRequestDtoValidator(),
            world.Access,
            repository.Object);

        // Act
        var act = () => useCase.ExecuteAsync(0, request, default);

        // Assert
        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("USER_NOT_FOUND");
        repository.Verify(x => x.UserExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Validation fails when startDate > endDate (from > to).
    /// <summary>Rejects inverted date ranges before executing database queries.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenStartDateAfterEndDate_ThrowsValidationException()
    {
        // Arrange
        var world = new ModerationTestContext();
        var repository = new Mock<IUserModerationRepository>();
        var request = new GetUserAuditLogsRequestDto
        {
            StartDate = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            Page = 1,
            PageSize = 20
        };

        var useCase = new GetUserAuditLogsUseCase(
            new GetUserAuditLogsRequestDtoValidator(),
            world.Access,
            repository.Object);

        // Act
        var act = () => useCase.ExecuteAsync(12, request, default);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
        repository.Verify(x => x.UserExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: Missing USER_VIEW_AUDIT permission throws 403 FORBIDDEN.
    /// <summary>Requires USER_VIEW_AUDIT effective permission for moderators.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutUserViewAuditPermission_ThrowsForbiddenException()
    {
        // Arrange
        var world = new ModerationTestContext();
        world.Accounts.Setup(x => x.GetEffectivePermissionsAsync(3, "MOD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["USER_SUSPEND"]);

        var repository = new Mock<IUserModerationRepository>();
        var request = new GetUserAuditLogsRequestDto { Page = 1, PageSize = 20 };

        var useCase = new GetUserAuditLogsUseCase(
            new GetUserAuditLogsRequestDtoValidator(),
            world.Access,
            repository.Object);

        // Act
        var act = () => useCase.ExecuteAsync(12, request, default);

        // Assert
        var exception = await act.Should().ThrowAsync<ForbiddenException>();
        exception.Which.ErrorCode.Should().Be("FORBIDDEN");
        repository.Verify(x => x.UserExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
