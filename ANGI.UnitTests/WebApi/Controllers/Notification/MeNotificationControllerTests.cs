using ANGI.Application.Common.Interfaces.UseCases.Notification;
using ANGI.Application.DTOs.Notification;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Notification;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Notification;

public sealed class MeNotificationControllerTests
{
    private readonly Mock<IGetNotificationsUseCase> _getNotificationsUseCase = new();
    private readonly Mock<IGetUnreadNotificationCountUseCase> _getUnreadNotificationCountUseCase = new();

    private MeNotificationController CreateController() =>
        new(_getNotificationsUseCase.Object, _getUnreadNotificationCountUseCase.Object);

    [Fact]
    public void Controller_ShouldRequireAuthorization()
    {
        var attributes = typeof(MeNotificationController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true);
        attributes.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetNotifications_ShouldCallUseCaseAndReturnOk()
    {
        // Arrange
        var request = new GetNotificationsRequestDto { Page = 1, PageSize = 20 };
        var responseData = new GetNotificationsResponseDto
        {
            Items = new List<NotificationDto>
            {
                new()
                {
                    Id = 100,
                    Type = "BLOG_COMMENTED",
                    Title = "Bình luận mới",
                    Body = "Chào bạn",
                    Data = new { blogId = 10 },
                    CreatedAt = DateTime.UtcNow
                }
            },
            Page = 1,
            PageSize = 20,
            TotalItems = 1,
            TotalPages = 1,
            UnreadCount = 1
        };

        _getNotificationsUseCase
            .Setup(u => u.ExecuteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(responseData);

        var controller = CreateController();

        // Act
        var actionResult = await controller.GetNotifications(request, CancellationToken.None);

        // Assert
        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var apiResponse = okResult.Value.Should().BeOfType<ApiResponse<GetNotificationsResponseDto>>().Subject;
        apiResponse.Success.Should().BeTrue();
        apiResponse.Data.Should().BeEquivalentTo(responseData);
    }

    [Fact]
    public async Task GetUnreadCount_ShouldCallUseCaseAndReturnOk()
    {
        // Arrange
        var responseData = new GetUnreadNotificationCountResponseDto { UnreadCount = 5 };

        _getUnreadNotificationCountUseCase
            .Setup(u => u.ExecuteAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(responseData);

        var controller = CreateController();

        // Act
        var actionResult = await controller.GetUnreadCount(CancellationToken.None);

        // Assert
        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var apiResponse = okResult.Value.Should().BeOfType<ApiResponse<GetUnreadNotificationCountResponseDto>>().Subject;
        apiResponse.Success.Should().BeTrue();
        apiResponse.Data.Should().BeEquivalentTo(responseData);
    }
}
