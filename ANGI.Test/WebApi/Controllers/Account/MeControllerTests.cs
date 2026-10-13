using ANGI.Application.Common.Interfaces.UseCases.Account;
using ANGI.Application.DTOs.Account;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Account;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.Test.WebApi.Controllers.Account;

public sealed class MeControllerTests
{
    private readonly Mock<IGetMyProfileUseCase> _getMyProfileUseCase = new();
    private readonly Mock<IUpdateMyProfileUseCase> _updateMyProfileUseCase = new();

    private MeController CreateController() =>
        new(_getMyProfileUseCase.Object, _updateMyProfileUseCase.Object);

    [Fact]
    public void Controller_ShouldRequireAuthorization()
    {
        var attributes = typeof(MeController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true);
        attributes.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetProfile_ShouldCallUseCaseAndReturnOk()
    {
        // Arrange
        var profile = new UserProfileDto
        {
            Id = 12,
            Email = "an.nguyen@gmail.com",
            DisplayName = "Nguyễn An",
            Role = "TRAVELER"
        };

        _getMyProfileUseCase.Setup(u => u.ExecuteAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        var controller = CreateController();

        // Act
        var actionResult = await controller.GetProfile(CancellationToken.None);

        // Assert
        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var response = okResult.Value.Should().BeOfType<ApiResponse<UserProfileDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeEquivalentTo(profile);
    }

    [Fact]
    public async Task UpdateProfile_ShouldCallUseCaseAndReturnOk()
    {
        // Arrange
        var request = new UpdateProfileRequestDto { DisplayName = "An Nguyễn Mới" };
        var updatedProfile = new UserProfileDto
        {
            Id = 12,
            DisplayName = "An Nguyễn Mới",
            Role = "TRAVELER"
        };

        _updateMyProfileUseCase.Setup(u => u.ExecuteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedProfile);

        var controller = CreateController();

        // Act
        var actionResult = await controller.UpdateProfile(request, CancellationToken.None);

        // Assert
        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var response = okResult.Value.Should().BeOfType<ApiResponse<UserProfileDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeEquivalentTo(updatedProfile);
    }
}
