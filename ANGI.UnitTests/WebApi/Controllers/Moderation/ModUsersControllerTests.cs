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

/// <summary>Verifies MOD-U3 controller delegation and HTTP 201 response wrapper.</summary>
public sealed class ModUsersControllerTests
{
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
            useCase.Object);

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

    [Fact]
    public void ModUsersController_RequiresModeratorOrAdminRole()
    {
        var attribute = (AuthorizeAttribute)Attribute.GetCustomAttribute(typeof(ModUsersController), typeof(AuthorizeAttribute))!;
        attribute.Should().NotBeNull();
        attribute.Roles.Should().Contain("MOD");
        attribute.Roles.Should().Contain("ADMIN");
    }
}
