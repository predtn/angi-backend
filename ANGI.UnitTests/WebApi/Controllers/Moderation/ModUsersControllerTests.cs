using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Moderation;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
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
        var getUsers = new Mock<IGetUsersUseCase>();
        var getUserDetail = new Mock<IGetUserDetailUseCase>();
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

        var controller = new ModUsersController(getUsers.Object, getUserDetail.Object, unbanUser.Object);

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

    // TEST-02: Require MOD, ADMIN or MODERATOR role on controller
    [Fact]
    public void ModUsersController_RequiresModeratorRole()
    {
        var attribute = (AuthorizeAttribute)Attribute.GetCustomAttribute(typeof(ModUsersController), typeof(AuthorizeAttribute))!;
        attribute.Roles.Should().Contain("MOD");
        attribute.Roles.Should().Contain("ADMIN");
    }
}
