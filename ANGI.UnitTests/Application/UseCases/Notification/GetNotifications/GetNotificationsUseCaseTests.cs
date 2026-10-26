using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Notification;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Notification;
using ANGI.Application.UseCases.Notification.GetNotifications;
using ANGI.Application.UseCases.Validators.Notification;
using ANGI.Domain.Entities;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Notification.GetNotifications;

public sealed class GetNotificationsUseCaseTests
{
    private readonly IValidator<GetNotificationsRequestDto> _validator = new GetNotificationsRequestDtoValidator();
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<INotificationRepository> _notificationRepository = new();

    private GetNotificationsUseCase CreateUseCase() =>
        new(_validator, _currentUserService.Object, _notificationRepository.Object);

    // Happy Path 1: Có dữ liệu, đúng phân trang và unreadCount
    // TEST-01: Verify ExecuteAsync when notifications exist; should return paginated list and unread count.
    /// <summary>Verify ExecuteAsync when notifications exist; should return paginated list and unread count.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenNotificationsExist_ShouldReturnPaginatedListAndUnreadCount()
    {
        // Arrange
        const int userId = 42;
        _currentUserService.Setup(s => s.UserId).Returns(userId);

        var request = new GetNotificationsRequestDto
        {
            Page = 1,
            PageSize = 10,
            IsRead = false,
            Type = "BLOG_COMMENTED"
        };

        var notifications = new List<Domain.Entities.Notification>
        {
            new()
            {
                Id = 501,
                RecipientId = userId,
                Type = "BLOG_COMMENTED",
                Title = "Có bình luận mới",
                Body = "Bình viết: \"Quán này ngon thật!\"",
                Data = "{\"blogId\":45,\"commentId\":302}",
                ReadAt = null,
                CreatedAt = new DateTime(2026, 10, 2, 7, 10, 0, DateTimeKind.Utc)
            },
            new()
            {
                Id = 502,
                RecipientId = userId,
                Type = "BLOG_COMMENTED",
                Title = "Có bình luận mới 2",
                Body = "An viết: \"Tuyệt vời!\"",
                Data = "{\"blogId\":45,\"commentId\":303}",
                ReadAt = null,
                CreatedAt = new DateTime(2026, 10, 2, 7, 15, 0, DateTimeKind.Utc)
            }
        };

        _notificationRepository
            .Setup(r => r.GetNotificationsAsync(userId, request.IsRead, request.Type, 1, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync((notifications, 25L));

        _notificationRepository
            .Setup(r => r.GetUnreadCountAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(10);
        result.TotalItems.Should().Be(25L);
        result.TotalPages.Should().Be(3); // ceil(25 / 10) = 3
        result.UnreadCount.Should().Be(7);
        result.Items.Should().HaveCount(2);

        var firstItem = result.Items[0];
        firstItem.Id.Should().Be(501);
        firstItem.Type.Should().Be("BLOG_COMMENTED");
        firstItem.Title.Should().Be("Có bình luận mới");
        firstItem.Body.Should().Be("Bình viết: \"Quán này ngon thật!\"");
        firstItem.ReadAt.Should().BeNull();
        firstItem.Data.Should().NotBeNull();
    }

    // Happy Path 1: Danh sách rỗng
    // TEST-02: Verify ExecuteAsync when no notifications exist; should return empty list and zero total pages.
    /// <summary>Verify ExecuteAsync when no notifications exist; should return empty list and zero total pages.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenNoNotificationsExist_ShouldReturnEmptyListAndZeroTotalPages()
    {
        // Arrange
        const int userId = 42;
        _currentUserService.Setup(s => s.UserId).Returns(userId);

        var request = new GetNotificationsRequestDto
        {
            Page = 1,
            PageSize = 20
        };

        _notificationRepository
            .Setup(r => r.GetNotificationsAsync(userId, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<Domain.Entities.Notification>(), 0L));

        _notificationRepository
            .Setup(r => r.GetUnreadCountAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Items.Should().BeEmpty();
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
        result.TotalItems.Should().Be(0L);
        result.TotalPages.Should().Be(0);
        result.UnreadCount.Should().Be(0);
    }

    // Exception Path: Truyền sai tham số phân trang (Page = 0)
    // TEST-03: Verify ExecuteAsync when page is zero; should throw validation exception.
    /// <summary>Verify ExecuteAsync when page is zero; should throw validation exception.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenPageIsZero_ShouldThrowValidationException()
    {
        // Arrange
        _currentUserService.Setup(s => s.UserId).Returns(1);

        var request = new GetNotificationsRequestDto
        {
            Page = 0,
            PageSize = 20
        };

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*page*");
    }

    // Exception Path: Truyền sai pageSize (> 100)
    // TEST-04: Verify ExecuteAsync when page size exceeds100; should throw validation exception.
    /// <summary>Verify ExecuteAsync when page size exceeds100; should throw validation exception.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenPageSizeExceeds100_ShouldThrowValidationException()
    {
        // Arrange
        _currentUserService.Setup(s => s.UserId).Returns(1);

        var request = new GetNotificationsRequestDto
        {
            Page = 1,
            PageSize = 101
        };

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*pageSize*");
    }

    // Exception Path: Chưa đăng nhập (UserId == null)
    // TEST-05: Verify ExecuteAsync when unauthenticated; should throw unauthorized exception.
    /// <summary>Verify ExecuteAsync when unauthenticated; should throw unauthorized exception.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenUnauthenticated_ShouldThrowUnauthorizedException()
    {
        // Arrange
        _currentUserService.Setup(s => s.UserId).Returns((int?)null);

        var request = new GetNotificationsRequestDto
        {
            Page = 1,
            PageSize = 20
        };

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<UnauthorizedException>();
        ex.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }
}
