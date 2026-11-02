using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.UpdateBlogCommentStatus;
using ANGI.Application.UseCases.Validators.Moderation;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.UpdateBlogCommentStatus;

/// <summary>Verifies MOD-B3 blog comment moderation status transitions and side effects.</summary>
public sealed class UpdateBlogCommentStatusUseCaseTests
{
    private static UpdateBlogCommentStatusUseCase Create(ModerationTestContext world) => new(
        new UpdateBlogCommentStatusRequestDtoValidator(),
        world.Access,
        world.BlogModerationRepo.Object,
        world.UnitOfWork.Object,
        world.Audit.Object);

    // TEST-01: Happy Path - Successfully updates status and records audit and transaction commit
    [Theory]
    [InlineData("visible", ContentStatus.Visible, "COMMENT_RESTORED", 1)]
    [InlineData("hidden", ContentStatus.Hidden, "COMMENT_HIDDEN", -1)]
    [InlineData("removed", ContentStatus.Removed, "COMMENT_REMOVED", -1)]
    public async Task PersistsCommentStatusTransition_Successfully(
        string status,
        ContentStatus expectedStatus,
        string expectedAuditAction,
        int expectedCommentCountDelta)
    {
        var world = new ModerationTestContext();
        world.Comment.Status = expectedStatus == ContentStatus.Visible ? ContentStatus.Hidden : ContentStatus.Visible;

        var request = new UpdateBlogCommentStatusRequestDto
        {
            Status = status,
            Reason = "Bình luận vi phạm tiêu chuẩn cộng đồng"
        };

        var response = await Create(world).ExecuteAsync(302, request, default);

        response.CommentId.Should().Be(302);
        response.Status.Should().Be(status);
        world.Comment.Status.Should().Be(expectedStatus);

        world.BlogModerationRepo.Verify(x => x.AddToCommentCountAsync(world.Comment.BlogId, expectedCommentCountDelta, It.IsAny<CancellationToken>()), Times.Once);

        world.Audit.Verify(x => x.Add(It.Is<AuditLogEntry>(a =>
            a.Action == expectedAuditAction &&
            a.EntityType == AuditEntityTypes.BlogComment &&
            a.EntityId == 302 &&
            a.SubjectUserId == 12)), Times.Once);

        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Hidden to Removed does not alter comment count since neither is visible
    [Fact]
    public async Task HiddenToRemoved_DoesNotAlterCommentCount()
    {
        var world = new ModerationTestContext();
        world.Comment.Status = ContentStatus.Hidden;

        var request = new UpdateBlogCommentStatusRequestDto
        {
            Status = "removed",
            Reason = "Gỡ hẳn bình luận"
        };

        var response = await Create(world).ExecuteAsync(302, request, default);

        response.CommentId.Should().Be(302);
        response.Status.Should().Be("removed");
        world.Comment.Status.Should().Be(ContentStatus.Removed);

        world.BlogModerationRepo.Verify(x => x.AddToCommentCountAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        world.Audit.Verify(x => x.Add(It.Is<AuditLogEntry>(a => a.Action == AuditActions.CommentRemoved)), Times.Once);
    }

    // TEST-03: Idempotent - Transitioning to current status does not alter count or duplicate audit
    [Fact]
    public async Task RepeatedStatus_DoesNotDuplicateEffects()
    {
        var world = new ModerationTestContext();
        world.Comment.Status = ContentStatus.Visible;

        var request = new UpdateBlogCommentStatusRequestDto
        {
            Status = "visible",
            Reason = "Không thay đổi gì"
        };

        var response = await Create(world).ExecuteAsync(302, request, default);

        response.CommentId.Should().Be(302);
        response.Status.Should().Be("visible");
        world.BlogModerationRepo.Verify(x => x.AddToCommentCountAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        world.Audit.Verify(x => x.Add(It.IsAny<AuditLogEntry>()), Times.Never);
    }

    // TEST-04: Exception Path 1 - 404 Not Found when commentId does not exist
    [Fact]
    public async Task NonExistentComment_ThrowsNotFoundException()
    {
        var world = new ModerationTestContext();
        world.BlogModerationRepo.Setup(x => x.GetCommentByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Domain.Entities.BlogComment?)null);

        var request = new UpdateBlogCommentStatusRequestDto
        {
            Status = "hidden",
            Reason = "Bình luận không tồn tại"
        };

        var act = () => Create(world).ExecuteAsync(999, request, default);

        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("COMMENT_NOT_FOUND");
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: Exception Path 2 - 400 Validation Error when status is invalid
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid_status")]
    [InlineData("published")]
    [InlineData("ACTIVE")]
    public async Task InvalidStatus_ThrowsValidationException(string invalidStatus)
    {
        var world = new ModerationTestContext();

        var request = new UpdateBlogCommentStatusRequestDto
        {
            Status = invalidStatus,
            Reason = "Lý do hợp lệ"
        };

        var act = () => Create(world).ExecuteAsync(302, request, default);

        await act.Should().ThrowAsync<ValidationException>();
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-06: Exception Path 3 - 400 Validation Error when reason is empty or exceeds 1000 characters
    [Fact]
    public async Task EmptyReason_ThrowsValidationException()
    {
        var world = new ModerationTestContext();

        var request = new UpdateBlogCommentStatusRequestDto
        {
            Status = "hidden",
            Reason = ""
        };

        var act = () => Create(world).ExecuteAsync(302, request, default);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ExcessivelyLongReason_ThrowsValidationException()
    {
        var world = new ModerationTestContext();

        var request = new UpdateBlogCommentStatusRequestDto
        {
            Status = "hidden",
            Reason = new string('x', 1001)
        };

        var act = () => Create(world).ExecuteAsync(302, request, default);

        await act.Should().ThrowAsync<ValidationException>();
    }

    // TEST-07: Forbidden Path - Mod without BLOG_MODERATE permission throws ForbiddenException
    [Fact]
    public async Task MissingBlogModeratePermission_ThrowsForbiddenException()
    {
        var world = new ModerationTestContext();
        world.Accounts.Setup(x => x.GetEffectivePermissionsAsync(3, "MOD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["RESTAURANT_MODERATE"]);

        var request = new UpdateBlogCommentStatusRequestDto
        {
            Status = "hidden",
            Reason = "Không đủ quyền"
        };

        var act = () => Create(world).ExecuteAsync(302, request, default);

        var ex = await act.Should().ThrowAsync<ForbiddenException>();
        ex.Which.ErrorCode.Should().Be("FORBIDDEN");
    }
}
