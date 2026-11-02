using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.UpdateReviewReplyStatus;
using ANGI.Application.UseCases.Validators.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;
using Xunit;

namespace ANGI.UnitTests.Application.UseCases.Moderation.UpdateReviewReplyStatus;

public sealed class UpdateReviewReplyStatusUseCaseTests
{
    private readonly ModerationTestContext _context = new();
    private readonly Mock<IReviewModerationRepository> _repository = new();

    private UpdateReviewReplyStatusUseCase CreateUseCase() => new(
        new UpdateReviewReplyStatusRequestDtoValidator(),
        _context.Access,
        _repository.Object,
        _context.Audit.Object,
        _context.UnitOfWork.Object);

    [Theory]
    [InlineData(ContentStatus.Visible, "hidden", ContentStatus.Hidden, AuditActions.ReviewReplyHidden)]
    [InlineData(ContentStatus.Visible, "removed", ContentStatus.Removed, AuditActions.ReviewReplyRemoved)]
    [InlineData(ContentStatus.Hidden, "visible", ContentStatus.Visible, AuditActions.ReviewReplyRestored)]
    [InlineData(ContentStatus.Removed, "visible", ContentStatus.Visible, AuditActions.ReviewReplyRestored)]
    public async Task ExecuteAsync_HappyPath_UpdatesReplyStatus(
        ContentStatus initialStatus,
        string targetStatusStr,
        ContentStatus expectedStatus,
        string expectedAuditAction)
    {
        // Arrange
        const long reviewId = 640;
        var reply = new ReviewReply
        {
            ReviewId = reviewId,
            ReplierId = 12,
            Content = "Cảm ơn bạn đã phản hồi!",
            Status = initialStatus
        };

        var review = new RestaurantReview
        {
            Id = reviewId,
            RestaurantId = 7,
            UserId = 10,
            Rating = 5,
            Status = ContentStatus.Visible,
            Reply = reply
        };

        _repository.Setup(x => x.LockReviewWithReplyAsync(reviewId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(review);

        var request = new UpdateReviewReplyStatusRequestDto
        {
            Status = targetStatusStr,
            Reason = "Nội dung phản hồi không đúng mực"
        };

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(reviewId, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.ReviewId.Should().Be(reviewId);
        result.ReplyStatus.Should().Be(targetStatusStr);
        reply.Status.Should().Be(expectedStatus);

        _context.Audit.Verify(x => x.Add(It.Is<AuditLogEntry>(entry =>
            entry.Action == expectedAuditAction &&
            entry.EntityType == AuditEntityTypes.ReviewReply &&
            entry.EntityId == reviewId &&
            entry.SubjectUserId == 12)), Times.Once);

        _context.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _context.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsNotFound_WhenReviewDoesNotExist()
    {
        // Arrange
        const long nonExistentReviewId = 9999;
        _repository.Setup(x => x.LockReviewWithReplyAsync(nonExistentReviewId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RestaurantReview?)null);

        var request = new UpdateReviewReplyStatusRequestDto
        {
            Status = "hidden",
            Reason = "Vi phạm nội quy"
        };

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(nonExistentReviewId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("REVIEW_NOT_FOUND");
        _context.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsBadRequest_WhenReviewHasNoReply()
    {
        // Arrange
        const long reviewId = 640;
        var reviewWithoutReply = new RestaurantReview
        {
            Id = reviewId,
            RestaurantId = 7,
            UserId = 10,
            Rating = 5,
            Status = ContentStatus.Visible,
            Reply = null // No reply from owner
        };

        _repository.Setup(x => x.LockReviewWithReplyAsync(reviewId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reviewWithoutReply);

        var request = new UpdateReviewReplyStatusRequestDto
        {
            Status = "hidden",
            Reason = "Vi phạm nội quy"
        };

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(reviewId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<BadRequestException>();
        ex.Which.ErrorCode.Should().Be("VALIDATION_FAILED");
        ex.Which.Message.Should().Be("Bài đánh giá chưa có phản hồi từ chủ nhà hàng.");
        _context.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("", "Lý do hợp lệ", "Vui lòng chọn trạng thái phản hồi.")]
    [InlineData("pending", "Lý do hợp lệ", "Trạng thái phản hồi không hợp lệ.")]
    [InlineData("hidden", "", "Vui lòng nhập lý do.")]
    public async Task ExecuteAsync_ThrowsValidationException_WhenRequestIsInvalid(
        string status,
        string reason,
        string expectedError)
    {
        // Arrange
        var request = new UpdateReviewReplyStatusRequestDto
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
