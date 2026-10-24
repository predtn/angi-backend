using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Notification;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Notification.GetUnreadNotificationCount;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Notification.GetUnreadNotificationCount;

public sealed class GetUnreadNotificationCountUseCaseTests
{
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<INotificationRepository> _notificationRepository = new();

    private GetUnreadNotificationCountUseCase CreateUseCase() =>
        new(_currentUserService.Object, _notificationRepository.Object);

    // Happy Path 2: Đếm đúng số lượng thông báo chưa đọc
    [Fact]
    public async Task ExecuteAsync_WhenAuthenticated_ShouldReturnCorrectUnreadCount()
    {
        // Arrange
        const int userId = 10;
        const int expectedCount = 5;

        _currentUserService.Setup(s => s.UserId).Returns(userId);
        _notificationRepository
            .Setup(r => r.GetUnreadCountAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedCount);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.UnreadCount.Should().Be(expectedCount);
    }

    // Exception Path: Chưa đăng nhập (UserId == null)
    [Fact]
    public async Task ExecuteAsync_WhenUnauthenticated_ShouldThrowUnauthorizedException()
    {
        // Arrange
        _currentUserService.Setup(s => s.UserId).Returns((int?)null);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<UnauthorizedException>();
        ex.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }
}
