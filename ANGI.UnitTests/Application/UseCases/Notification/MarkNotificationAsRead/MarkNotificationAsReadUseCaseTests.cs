using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Notification;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Notification;
using ANGI.Application.UseCases.Notification.MarkNotificationAsRead;
using ANGI.Application.UseCases.Validators.Notification;
using ANGI.Domain.Entities;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Notification.MarkNotificationAsRead;

public sealed class MarkNotificationAsReadUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private const int TestUserId = 100;
    private const int OtherUserId = 200;

    // TEST-01: Happy Path - Đánh dấu một hoặc nhiều thông báo cụ thể thành đã đọc
    [Fact]
    public async Task ExecuteAsync_WithSpecificIds_ShouldMarkNotificationsAsReadAndReturnUpdatedCount()
    {
        // Arrange
        var ids = new List<long> { 501, 502 };
        var notification1 = new Domain.Entities.Notification
        {
            Id = 501,
            RecipientId = TestUserId,
            Type = "BLOG_COMMENTED",
            Title = "Bình luận mới",
            ReadAt = null
        };
        var notification2 = new Domain.Entities.Notification
        {
            Id = 502,
            RecipientId = TestUserId,
            Type = "REVIEW_REPLIED",
            Title = "Phản hồi đánh giá",
            ReadAt = null
        };

        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService.SetupGet(u => u.UserId).Returns(TestUserId);

        var notificationRepository = new Mock<INotificationRepository>();
        notificationRepository.Setup(r => r.GetUnreadNotificationsToMarkReadAsync(
                TestUserId,
                ids,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Domain.Entities.Notification> { notification1, notification2 });

        var unitOfWork = new Mock<IUnitOfWork>();

        var useCase = new MarkNotificationAsReadUseCase(
            new MarkNotificationAsReadRequestDtoValidator(),
            currentUserService.Object,
            notificationRepository.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));

        var request = new MarkNotificationAsReadRequestDto
        {
            Ids = ids,
            All = false
        };

        // Act
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Updated.Should().Be(2);

        notification1.ReadAt.Should().Be(Now);
        notification2.ReadAt.Should().Be(Now);

        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Happy Path - Đánh dấu tất cả thông báo của người dùng là đã đọc (All = true)
    [Fact]
    public async Task ExecuteAsync_WithAllTrue_ShouldMarkAllUnreadNotificationsAsRead()
    {
        // Arrange
        var unreadNotifications = new List<Domain.Entities.Notification>
        {
            new() { Id = 1, RecipientId = TestUserId, Type = "VERIFICATION_APPROVED", Title = "Đã duyệt", ReadAt = null },
            new() { Id = 2, RecipientId = TestUserId, Type = "ROADMAP_GENERATED", Title = "Lộ trình mới", ReadAt = null },
            new() { Id = 3, RecipientId = TestUserId, Type = "MENU_REJECTED", Title = "Từ chối menu", ReadAt = null }
        };

        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService.SetupGet(u => u.UserId).Returns(TestUserId);

        var notificationRepository = new Mock<INotificationRepository>();
        notificationRepository.Setup(r => r.GetUnreadNotificationsToMarkReadAsync(
                TestUserId,
                null,
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(unreadNotifications);

        var unitOfWork = new Mock<IUnitOfWork>();

        var useCase = new MarkNotificationAsReadUseCase(
            new MarkNotificationAsReadRequestDtoValidator(),
            currentUserService.Object,
            notificationRepository.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));

        var request = new MarkNotificationAsReadRequestDto
        {
            All = true
        };

        // Act
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Updated.Should().Be(3);

        unreadNotifications.Should().OnlyContain(n => n.ReadAt == Now);
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Exception Path 1 - Truyền danh sách ID trống và All = false
    [Fact]
    public async Task ExecuteAsync_WithEmptyIdsAndAllFalse_ShouldThrowValidationException()
    {
        // Arrange
        var useCase = new MarkNotificationAsReadUseCase(
            new MarkNotificationAsReadRequestDtoValidator(),
            Mock.Of<ICurrentUserService>(),
            Mock.Of<INotificationRepository>(),
            Mock.Of<IUnitOfWork>(),
            new FixedTimeProvider(Now));

        var request = new MarkNotificationAsReadRequestDto
        {
            Ids = new List<long>(),
            All = false
        };

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
    }

    // TEST-04: Exception Path 1b - Truyền Ids null và All null
    [Fact]
    public async Task ExecuteAsync_WithNullIdsAndNullAll_ShouldThrowValidationException()
    {
        // Arrange
        var useCase = new MarkNotificationAsReadUseCase(
            new MarkNotificationAsReadRequestDtoValidator(),
            Mock.Of<ICurrentUserService>(),
            Mock.Of<INotificationRepository>(),
            Mock.Of<IUnitOfWork>(),
            new FixedTimeProvider(Now));

        var request = new MarkNotificationAsReadRequestDto
        {
            Ids = null,
            All = null
        };

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
    }

    // TEST-05: Exception Path 1c - Truyền quá 100 ID (> MaxIds)
    [Fact]
    public async Task ExecuteAsync_WithMoreThan100Ids_ShouldThrowValidationException()
    {
        // Arrange
        var useCase = new MarkNotificationAsReadUseCase(
            new MarkNotificationAsReadRequestDtoValidator(),
            Mock.Of<ICurrentUserService>(),
            Mock.Of<INotificationRepository>(),
            Mock.Of<IUnitOfWork>(),
            new FixedTimeProvider(Now));

        var request = new MarkNotificationAsReadRequestDto
        {
            Ids = Enumerable.Range(1, 101).Select(i => (long)i).ToList(),
            All = false
        };

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
    }

    // TEST-06: Exception Path 1d - Truyền ID không hợp lệ (<= 0)
    [Fact]
    public async Task ExecuteAsync_WithNonPositiveId_ShouldThrowValidationException()
    {
        // Arrange
        var useCase = new MarkNotificationAsReadUseCase(
            new MarkNotificationAsReadRequestDtoValidator(),
            Mock.Of<ICurrentUserService>(),
            Mock.Of<INotificationRepository>(),
            Mock.Of<IUnitOfWork>(),
            new FixedTimeProvider(Now));

        var request = new MarkNotificationAsReadRequestDto
        {
            Ids = new List<long> { 501, 0, -5 },
            All = false
        };

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
    }

    // TEST-07: Exception Path 2 - Thông báo không tồn tại hoặc không thuộc sở hữu của user
    // Nghiệp vụ quy định: "Bỏ qua id không thuộc người dùng", không ném lỗi 403/404, trả về updated = 0
    [Fact]
    public async Task ExecuteAsync_WhenIdsDoNotBelongToUserOrDoNotExist_ShouldIgnoreThemAndReturnUpdatedZero()
    {
        // Arrange
        var requestedIds = new List<long> { 999, 1000 };

        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService.SetupGet(u => u.UserId).Returns(TestUserId);

        var notificationRepository = new Mock<INotificationRepository>();
        // Repository filters by RecipientId == TestUserId, so it returns empty list
        notificationRepository.Setup(r => r.GetUnreadNotificationsToMarkReadAsync(
                TestUserId,
                requestedIds,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Domain.Entities.Notification>());

        var unitOfWork = new Mock<IUnitOfWork>();

        var useCase = new MarkNotificationAsReadUseCase(
            new MarkNotificationAsReadRequestDtoValidator(),
            currentUserService.Object,
            notificationRepository.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));

        var request = new MarkNotificationAsReadRequestDto
        {
            Ids = requestedIds,
            All = false
        };

        // Act
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Updated.Should().Be(0);

        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-08: Exception Path 2b - Hỗn hợp ID: Một phần thuộc user, một phần không thuộc user
    [Fact]
    public async Task ExecuteAsync_WithMixedOwnershipIds_ShouldOnlyUpdateNotificationsBelongingToUser()
    {
        // Arrange
        var requestedIds = new List<long> { 501, 999 }; // 501 thuộc TestUser, 999 không thuộc
        var ownNotification = new Domain.Entities.Notification
        {
            Id = 501,
            RecipientId = TestUserId,
            Type = "REPORT_RESOLVED",
            Title = "Báo cáo đã xử lý",
            ReadAt = null
        };

        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService.SetupGet(u => u.UserId).Returns(TestUserId);

        var notificationRepository = new Mock<INotificationRepository>();
        // Repository only returns own unread notification
        notificationRepository.Setup(r => r.GetUnreadNotificationsToMarkReadAsync(
                TestUserId,
                requestedIds,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Domain.Entities.Notification> { ownNotification });

        var unitOfWork = new Mock<IUnitOfWork>();

        var useCase = new MarkNotificationAsReadUseCase(
            new MarkNotificationAsReadRequestDtoValidator(),
            currentUserService.Object,
            notificationRepository.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));

        var request = new MarkNotificationAsReadRequestDto
        {
            Ids = requestedIds,
            All = false
        };

        // Act
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Updated.Should().Be(1);
        ownNotification.ReadAt.Should().Be(Now);

        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-09: Exception Path 3 - Chưa đăng nhập (UserId is null) -> ném UnauthorizedException
    [Fact]
    public async Task ExecuteAsync_WhenNotAuthenticated_ShouldThrowUnauthorizedException()
    {
        // Arrange
        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService.SetupGet(u => u.UserId).Returns((int?)null);

        var useCase = new MarkNotificationAsReadUseCase(
            new MarkNotificationAsReadRequestDtoValidator(),
            currentUserService.Object,
            Mock.Of<INotificationRepository>(),
            Mock.Of<IUnitOfWork>(),
            new FixedTimeProvider(Now));

        var request = new MarkNotificationAsReadRequestDto
        {
            Ids = new List<long> { 501 },
            All = false
        };

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<UnauthorizedException>();
        ex.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTime now)
        {
            _now = new DateTimeOffset(now);
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
