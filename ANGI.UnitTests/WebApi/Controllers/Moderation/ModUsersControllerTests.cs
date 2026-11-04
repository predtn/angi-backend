using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Moderation;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Moderation;

/// <summary>Verifies thin controller delegation and role restrictions for ModUsersController.</summary>
public sealed class ModUsersControllerTests
{
    // TEST-01: MOD-U4 unban delegates route identifier, request body, and cancellation token.
    [Fact]
    public async Task Unban_DelegatesToUseCase_ReturnsOkEnvelope()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var unbanUser = new Mock<IUnbanUserUseCase>();

        var request = new UnbanUserRequestDto { LiftReason = "Lý do gỡ lệnh cấm" };
        var expectedCard = new AdminUserCardDto
        {
            Id = 12,
            Email = "user@test.local",
            DisplayName = "Test User",
            Role = "TRAVELER",
            Status = "active",
            SuspendedUntil = null,
            CreatedAt = new DateTime(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc)
        };

        unbanUser
            .Setup(x => x.ExecuteAsync(12, request, cancellation.Token))
            .ReturnsAsync(expectedCard);

        var controller = new ModUsersController(
            Mock.Of<IGetUsersUseCase>(),
            Mock.Of<IGetUserDetailUseCase>(),
            Mock.Of<ICreateUserSanctionUseCase>(),
            unbanUser.Object);

        // Act
        var actionResult = await controller.Unban(12, request, cancellation.Token);

        // Assert
        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(200);

        var envelope = okResult.Value.Should().BeOfType<ApiResponse<AdminUserCardDto>>().Subject;
        envelope.Success.Should().BeTrue();
        envelope.Data.Should().BeSameAs(expectedCard);

        unbanUser.Verify(x => x.ExecuteAsync(12, request, cancellation.Token), Times.Once);
    }

    // TEST-02: MOD-U3 delegates to the sanction use case and answers 201 Created.
    [Fact]
    public async Task CreateSanction_Returns201Created_WithSanctionDto()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var useCase = new Mock<ICreateUserSanctionUseCase>();
        var request = new CreateUserSanctionRequestDto
        {
            Type = "suspend",
            DurationDays = 7,
            Reason = "Spamming"
        };
        var expectedSanction = new SanctionDto
        {
            Id = 42,
            Type = "suspend",
            Reason = "Spamming"
        };

        useCase.Setup(x => x.ExecuteAsync(15, request, cts.Token))
            .ReturnsAsync(expectedSanction);

        var controller = new ModUsersController(
            Mock.Of<IGetUsersUseCase>(),
            Mock.Of<IGetUserDetailUseCase>(),
            useCase.Object,
            Mock.Of<IUnbanUserUseCase>());

        // Act
        var actionResult = await controller.CreateSanction(15, request, cts.Token);

        // Assert
        var result = actionResult as ObjectResult;
        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(StatusCodes.Status201Created);

        var response = result.Value as ApiResponse<SanctionDto>;
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expectedSanction);

        useCase.Verify(x => x.ExecuteAsync(15, request, cts.Token), Times.Once);
    }

    // TEST-03: Require MOD or ADMIN role on the controller.
    [Fact]
    public void ModUsersController_RequiresModeratorOrAdminRole()
    {
        var attribute = (AuthorizeAttribute)Attribute.GetCustomAttribute(typeof(ModUsersController), typeof(AuthorizeAttribute))!;
        attribute.Should().NotBeNull();
        attribute.Roles.Should().Contain("MOD");
        attribute.Roles.Should().Contain("ADMIN");
    }

    // TEST-04: MOD-U5 delegates userId, query filters, and cancellation token to GetUserAuditLogsUseCase.
    [Fact]
    public async Task GetAuditLogs_DelegatesToUseCase_ReturnsOkEnvelope()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var getUserAuditLogs = new Mock<IGetUserAuditLogsUseCase>();
        var request = new GetUserAuditLogsRequestDto { Page = 1, PageSize = 20 };
        var expectedPage = new ANGI.Application.Common.Models.Paged<AuditLogDto>
        {
            Items = [new AuditLogDto { Id = 9921, Action = "USER_SUSPENDED" }],
            Page = 1,
            PageSize = 20,
            TotalItems = 1,
            TotalPages = 1
        };

        getUserAuditLogs
            .Setup(x => x.ExecuteAsync(12, request, cts.Token))
            .ReturnsAsync(expectedPage);

        var controller = new ModUsersController(
            Mock.Of<IGetUsersUseCase>(),
            Mock.Of<IGetUserDetailUseCase>(),
            Mock.Of<ICreateUserSanctionUseCase>(),
            Mock.Of<IUnbanUserUseCase>(),
            getUserAuditLogs.Object);

        // Act
        var actionResult = await controller.GetAuditLogs(12, request, cts.Token);

        // Assert
        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var envelope = okResult.Value.Should().BeOfType<ApiResponse<ANGI.Application.Common.Models.Paged<AuditLogDto>>>().Subject;
        envelope.Success.Should().BeTrue();
        envelope.Data.Should().BeSameAs(expectedPage);

        getUserAuditLogs.Verify(x => x.ExecuteAsync(12, request, cts.Token), Times.Once);
    }
}
