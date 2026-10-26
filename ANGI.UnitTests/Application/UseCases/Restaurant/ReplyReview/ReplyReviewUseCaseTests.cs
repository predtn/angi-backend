using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.ReplyReview;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.ReplyReview;

public sealed class ReplyReviewUseCaseTests
{
    private readonly Mock<IRestaurantRepository> _repositoryMock = new();
    private readonly Mock<ICurrentUserService> _currentUserServiceMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly ReplyReviewRequestDtoValidator _validator = new();

    private ReplyReviewUseCase CreateUseCase() =>
        new(_repositoryMock.Object, _currentUserServiceMock.Object, _unitOfWorkMock.Object, _validator);

    // TEST-01: Happy Path - Create new reply to review and send notification to traveler
    [Fact]
    public async Task ExecuteAsync_WhenCreatingNewReply_ShouldAddReplyAndNotificationAndSave()
    {
        // Arrange
        const int ownerId = 10;
        const int travelerId = 20;
        const long reviewId = 100L;
        const long restaurantId = 50L;

        var request = new ReplyReviewRequestDto
        {
            Content = "Cảm ơn bạn đã phản hồi!"
        };

        var restaurant = new ANGI.Domain.Entities.Restaurant
        {
            Id = restaurantId,
            OwnerId = ownerId,
            Name = "Quán Phở Thìn"
        };

        var review = new RestaurantReview
        {
            Id = reviewId,
            RestaurantId = restaurantId,
            Restaurant = restaurant,
            UserId = travelerId,
            Rating = 5,
            Content = "Phở rất ngon",
            Status = ContentStatus.Visible,
            Reply = null
        };

        _currentUserServiceMock.Setup(s => s.UserId).Returns(ownerId);
        _repositoryMock
            .Setup(r => r.GetReviewForReplyAsync(reviewId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(review);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(reviewId, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Content.Should().Be("Cảm ơn bạn đã phản hồi!");
        result.Status.Should().Be("visible");

        _repositoryMock.Verify(r => r.AddReviewReply(It.Is<ReviewReply>(reply =>
            reply.ReviewId == reviewId &&
            reply.ReplierId == ownerId &&
            reply.Content == "Cảm ơn bạn đã phản hồi!" &&
            reply.Status == ContentStatus.Visible)), Times.Once);

        _repositoryMock.Verify(r => r.AddNotification(It.Is<ANGI.Domain.Entities.Notification>(n =>
            n.RecipientId == travelerId &&
            n.Type == "REVIEW_REPLIED")), Times.Once);

        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Happy Path - Update existing reply to review
    [Fact]
    public async Task ExecuteAsync_WhenUpdatingExistingReply_ShouldUpdateContentWithoutDuplicateNotification()
    {
        // Arrange
        const int ownerId = 10;
        const int travelerId = 20;
        const long reviewId = 100L;
        const long restaurantId = 50L;

        var request = new ReplyReviewRequestDto
        {
            Content = "Nội dung phản hồi đã cập nhật!"
        };

        var restaurant = new ANGI.Domain.Entities.Restaurant
        {
            Id = restaurantId,
            OwnerId = ownerId,
            Name = "Quán Phở Thìn"
        };

        var existingReply = new ReviewReply
        {
            ReviewId = reviewId,
            ReplierId = ownerId,
            Content = "Nội dung cũ",
            Status = ContentStatus.Visible,
            CreatedAt = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc)
        };

        var review = new RestaurantReview
        {
            Id = reviewId,
            RestaurantId = restaurantId,
            Restaurant = restaurant,
            UserId = travelerId,
            Rating = 4,
            Content = "Phở tạm được",
            Status = ContentStatus.Visible,
            Reply = existingReply
        };

        _currentUserServiceMock.Setup(s => s.UserId).Returns(ownerId);
        _repositoryMock
            .Setup(r => r.GetReviewForReplyAsync(reviewId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(review);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(reviewId, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Content.Should().Be("Nội dung phản hồi đã cập nhật!");
        existingReply.Content.Should().Be("Nội dung phản hồi đã cập nhật!");

        _repositoryMock.Verify(r => r.AddReviewReply(It.IsAny<ReviewReply>()), Times.Never);
        _repositoryMock.Verify(r => r.AddNotification(It.IsAny<ANGI.Domain.Entities.Notification>()), Times.Never);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Exception Path 1 - Owner tries to reply to a review of another owner's restaurant (403 Forbidden)
    [Fact]
    public async Task ExecuteAsync_WhenReviewBelongsToAnotherOwner_ShouldThrowForbiddenException()
    {
        // Arrange
        const int currentOwnerId = 10;
        const int rivalOwnerId = 999;
        const long reviewId = 100L;

        var request = new ReplyReviewRequestDto { Content = "Cố tình trả lời quán đối thủ!" };

        var rivalRestaurant = new ANGI.Domain.Entities.Restaurant
        {
            Id = 55L,
            OwnerId = rivalOwnerId,
            Name = "Quán Đối Thủ"
        };

        var review = new RestaurantReview
        {
            Id = reviewId,
            RestaurantId = rivalRestaurant.Id,
            Restaurant = rivalRestaurant,
            UserId = 30,
            Rating = 1,
            Status = ContentStatus.Visible
        };

        _currentUserServiceMock.Setup(s => s.UserId).Returns(currentOwnerId);
        _repositoryMock
            .Setup(r => r.GetReviewForReplyAsync(reviewId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(review);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(reviewId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<ForbiddenException>();
        ex.Which.ErrorCode.Should().Be("FORBIDDEN");
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Exception Path 2 - ReviewId does not exist (404 Not Found)
    [Fact]
    public async Task ExecuteAsync_WhenReviewDoesNotExist_ShouldThrowNotFoundException()
    {
        // Arrange
        const int ownerId = 10;
        const long nonexistentReviewId = 99999L;
        var request = new ReplyReviewRequestDto { Content = "Trả lời review ảo" };

        _currentUserServiceMock.Setup(s => s.UserId).Returns(ownerId);
        _repositoryMock
            .Setup(r => r.GetReviewForReplyAsync(nonexistentReviewId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RestaurantReview?)null);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(nonexistentReviewId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("REVIEW_NOT_FOUND");
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: Exception Path 3 - Review is hidden by Mod (409 Conflict - REVIEW_NOT_VISIBLE)
    [Fact]
    public async Task ExecuteAsync_WhenReviewIsHidden_ShouldThrowConflictException()
    {
        // Arrange
        const int ownerId = 10;
        const long reviewId = 100L;

        var request = new ReplyReviewRequestDto { Content = "Trả lời review đã bị ẩn" };

        var restaurant = new ANGI.Domain.Entities.Restaurant
        {
            Id = 50L,
            OwnerId = ownerId,
            Name = "Quán Phở Thìn"
        };

        var review = new RestaurantReview
        {
            Id = reviewId,
            RestaurantId = restaurant.Id,
            Restaurant = restaurant,
            UserId = 20,
            Rating = 2,
            Status = ContentStatus.Hidden
        };

        _currentUserServiceMock.Setup(s => s.UserId).Returns(ownerId);
        _repositoryMock
            .Setup(r => r.GetReviewForReplyAsync(reviewId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(review);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(reviewId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<ConflictException>();
        ex.Which.ErrorCode.Should().Be("REVIEW_NOT_VISIBLE");
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-06: Exception Path 4 - User not logged in (401 Unauthorized)
    [Fact]
    public async Task ExecuteAsync_WhenUserNotAuthenticated_ShouldThrowUnauthorizedException()
    {
        // Arrange
        _currentUserServiceMock.Setup(s => s.UserId).Returns((int?)null);
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(1L, new ReplyReviewRequestDto { Content = "Chào bạn" }, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<UnauthorizedException>();
        ex.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    // TEST-07: Validation failure - Content is empty
    [Fact]
    public async Task ExecuteAsync_WhenContentIsEmpty_ShouldThrowValidationException()
    {
        // Arrange
        var request = new ReplyReviewRequestDto { Content = "" };
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(1L, request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
    }
}
