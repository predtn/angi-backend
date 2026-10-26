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
    private readonly Mock<IMarkNotificationAsReadUseCase> _markNotificationAsReadUseCase = new();

    private MeNotificationController CreateController() =>
        new(
            _getNotificationsUseCase.Object,
            _getUnreadNotificationCountUseCase.Object,
            _markNotificationAsReadUseCase.Object);

    // TEST-01: Verify Controller should require authorization.
    /// <summary>Verify Controller should require authorization.</summary>
    [Fact]
    public void Controller_ShouldRequireAuthorization()
    {
        var attributes = typeof(MeNotificationController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true);
        attributes.Should().NotBeEmpty();
    }

    // TEST-02: Verify GetNotifications should call use case and return ok.
    /// <summary>Verify GetNotifications should call use case and return ok.</summary>
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

    // TEST-03: Verify GetUnreadCount should call use case and return ok.
    /// <summary>Verify GetUnreadCount should call use case and return ok.</summary>
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

    // TEST-04: Verify MarkAsRead with valid request; returns ok with api response.
    /// <summary>Verify MarkAsRead with valid request; returns ok with api response.</summary>
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

        _markNotificationAsReadUseCase.Setup(u => u.ExecuteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var controller = CreateController();

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

        _markNotificationAsReadUseCase.Verify(u => u.ExecuteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }
}
