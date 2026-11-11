using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.UpdateReviewStatus;
using ANGI.Application.UseCases.Validators.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;
using Xunit;

namespace ANGI.UnitTests.Application.UseCases.Moderation.UpdateReviewStatus;

public sealed class UpdateReviewStatusUseCaseTests
{
    private readonly ModerationTestContext _context = new();
    private readonly Mock<IReviewModerationRepository> _repository = new();

    private UpdateReviewStatusUseCase CreateUseCase() => new(
        new UpdateReviewStatusRequestDtoValidator(),
        _context.Access,
        _repository.Object,
        _context.Audit.Object,
        _context.UnitOfWork.Object);

    [Theory]
    [InlineData(ContentStatus.Visible, "hidden", ContentStatus.Hidden, AuditActions.ReviewHidden)]
    [InlineData(ContentStatus.Visible, "removed", ContentStatus.Removed, AuditActions.ReviewRemoved)]
    [InlineData(ContentStatus.Hidden, "visible", ContentStatus.Visible, AuditActions.ReviewRestored)]
    [InlineData(ContentStatus.Removed, "visible", ContentStatus.Visible, AuditActions.ReviewRestored)]
    public async Task ExecuteAsync_HappyPath_UpdatesStatusAndRecalculatesRating(
        ContentStatus initialStatus,
        string targetStatusStr,
        ContentStatus expectedStatus,
        string expectedAuditAction)
    {
        // Arrange
        const long reviewId = 640;
        const long restaurantId = 7;
        var restaurant = new ANGI.Domain.Entities.Restaurant
        {
            Id = restaurantId,
            RatingAvg = 4.5m,
            RatingCount = 2
        };

        var review = new RestaurantReview
        {
            Id = reviewId,
            RestaurantId = restaurantId,
            UserId = 10,
            Rating = 5,
            Status = initialStatus,
            Restaurant = restaurant
        };

        _repository.Setup(x => x.LockReviewWithRestaurantAsync(reviewId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(review);

        _repository.Setup(x => x.GetVisibleRatingStatsAsync(restaurantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((2, 9)); // initial visible: 2 reviews, sum = 9

        var request = new UpdateReviewStatusRequestDto
        {
            Status = targetStatusStr,
            Reason = "Vi phạm tiêu chuẩn cộng đồng"
        };

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(reviewId, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.ReviewId.Should().Be(reviewId);
        result.Status.Should().Be(targetStatusStr);
        review.Status.Should().Be(expectedStatus);

        if (initialStatus == ContentStatus.Visible && expectedStatus != ContentStatus.Visible)
        {
            // Count becomes 1, sum becomes 4 -> avg = 4.00
            restaurant.RatingCount.Should().Be(1);
            restaurant.RatingAvg.Should().Be(4.00m);
        }
        else if (initialStatus != ContentStatus.Visible && expectedStatus == ContentStatus.Visible)
        {
            // Count becomes 3, sum becomes 14 -> avg = 4.67
            restaurant.RatingCount.Should().Be(3);
            restaurant.RatingAvg.Should().Be(4.67m);
        }

        _context.Audit.Verify(x => x.Add(It.Is<AuditLogEntry>(entry =>
            entry.Action == expectedAuditAction &&
            entry.EntityType == AuditEntityTypes.Review &&
            entry.EntityId == reviewId &&
            entry.SubjectUserId == 10)), Times.Once);

        _context.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _context.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsNotFound_WhenReviewDoesNotExist()
    {
        // Arrange
        const long nonExistentReviewId = 9999;
        _repository.Setup(x => x.LockReviewWithRestaurantAsync(nonExistentReviewId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RestaurantReview?)null);

        var request = new UpdateReviewStatusRequestDto
        {
            Status = "hidden",
            Reason = "Spam review"
        };

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(nonExistentReviewId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("REVIEW_NOT_FOUND");
        _context.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("", "Lý do hợp lệ", "Vui lòng chọn trạng thái đánh giá.")]
    [InlineData("pending", "Lý do hợp lệ", "Trạng thái đánh giá không hợp lệ.")]
    [InlineData("hidden", "", "Vui lòng nhập lý do.")]
    public async Task ExecuteAsync_ThrowsValidationException_WhenRequestIsInvalid(
        string status,
        string reason,
        string expectedError)
    {
        // Arrange
        var request = new UpdateReviewStatusRequestDto
        {
            Status = status,
            Reason = reason
        };

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(640, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().Contain(e => e.ErrorMessage == expectedError);
    }
}
