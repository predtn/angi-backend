using ANGI.Application.Common.Interfaces.UseCases.Notification;
using ANGI.Application.DTOs.Notification;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Notification;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Notification;

public sealed class MeNotificationControllerTests
{
    [Fact]
    public async Task MarkAsRead_WithValidRequest_ReturnsOkWithApiResponse()
    {
        // Arrange
        var request = new MarkNotificationAsReadRequestDto
        {
            Ids = new List<long> { 501, 502 },
            All = false
        };

        var expectedResponse = new MarkNotificationAsReadResponseDto
        {
            Updated = 2
        };

        var useCase = new Mock<IMarkNotificationAsReadUseCase>();
        useCase.Setup(u => u.ExecuteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var controller = new MeNotificationController(useCase.Object);

        // Act
        var result = await controller.MarkAsRead(request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<OkObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status200OK);

        var okResult = (OkObjectResult)result;
        okResult.Value.Should().BeOfType<ApiResponse<MarkNotificationAsReadResponseDto>>();

        var apiResponse = (ApiResponse<MarkNotificationAsReadResponseDto>)okResult.Value!;
        apiResponse.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.Updated.Should().Be(2);
        apiResponse.ErrorCode.Should().BeNull();
        apiResponse.Message.Should().BeNull();

        useCase.Verify(u => u.ExecuteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }
}
