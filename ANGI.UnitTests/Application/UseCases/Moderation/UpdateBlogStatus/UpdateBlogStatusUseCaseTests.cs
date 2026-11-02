using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.UpdateBlogStatus;
using ANGI.Application.UseCases.Validators.Moderation;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.UpdateBlogStatus;

/// <summary>Verifies MOD-B2 blog moderation status transitions and side effects.</summary>
public sealed class UpdateBlogStatusUseCaseTests
{
    private static UpdateBlogStatusUseCase Create(ModerationTestContext world) => new(
        new UpdateBlogStatusRequestDtoValidator(),
        world.Access,
        world.BlogModerationRepo.Object,
        world.UnitOfWork.Object,
        world.BlogEffects);

    // TEST-01: Happy Path - Successfully updates status and records audit, notification and transaction commit
    [Theory]
    [InlineData("published", BlogStatus.Published, "BLOG_RESTORED")]
    [InlineData("hidden", BlogStatus.Hidden, "BLOG_HIDDEN")]
    [InlineData("removed", BlogStatus.Removed, "BLOG_REMOVED")]
    public async Task PersistsStatusTransition_Successfully(string status, BlogStatus expectedStatus, string expectedAuditAction)
    {
        var world = new ModerationTestContext();
        world.Blog.Status = expectedStatus == BlogStatus.Published ? BlogStatus.Hidden : BlogStatus.Published;

        var request = new UpdateBlogStatusRequestDto
        {
            Status = status,
            Reason = "Vi phạm chính sách cộng đồng"
        };

        var response = await Create(world).ExecuteAsync(45, request, default);

        response.BlogId.Should().Be(45);
        response.Status.Should().Be(status);
        world.Blog.Status.Should().Be(expectedStatus);

        world.Audit.Verify(x => x.Add(It.Is<AuditLogEntry>(a =>
            a.Action == expectedAuditAction &&
            a.EntityType == AuditEntityTypes.Blog &&
            a.EntityId == 45 &&
            a.SubjectUserId == 12)), Times.Once);

        world.BlogModerationRepo.Verify(x => x.AddNotification(It.Is<Domain.Entities.Notification>(n =>
            n.RecipientId == 12 &&
            n.Body == "Vi phạm chính sách cộng đồng")), Times.Once);

        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Idempotent - Transitioning to current status does not duplicate audit or notification
    [Fact]
    public async Task RepeatedStatus_DoesNotDuplicateEffects()
    {
        var world = new ModerationTestContext();
        world.Blog.Status = BlogStatus.Published;

        var request = new UpdateBlogStatusRequestDto
        {
            Status = "published",
            Reason = "Không thay đổi gì"
        };

        var response = await Create(world).ExecuteAsync(45, request, default);

        response.BlogId.Should().Be(45);
        response.Status.Should().Be("published");
        world.Audit.Verify(x => x.Add(It.IsAny<AuditLogEntry>()), Times.Never);
        world.BlogModerationRepo.Verify(x => x.AddNotification(It.IsAny<Domain.Entities.Notification>()), Times.Never);
    }

    // TEST-03: Exception Path 1 - 404 Not Found when blogId does not exist
    [Fact]
    public async Task NonExistentBlog_ThrowsNotFoundException()
    {
        var world = new ModerationTestContext();
        world.BlogModerationRepo.Setup(x => x.GetBlogByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Domain.Entities.Blog?)null);

        var request = new UpdateBlogStatusRequestDto
        {
            Status = "hidden",
            Reason = "Blog không tồn tại"
        };

        var act = () => Create(world).ExecuteAsync(999, request, default);

        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("BLOG_NOT_FOUND");
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Exception Path 2 - 400 Validation Error when status is invalid
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid_status")]
    [InlineData("ACTIVE")]
    [InlineData("DELETED")]
    public async Task InvalidStatus_ThrowsValidationException(string invalidStatus)
    {
        var world = new ModerationTestContext();

        var request = new UpdateBlogStatusRequestDto
        {
            Status = invalidStatus,
            Reason = "Lý do hợp lệ"
        };

        var act = () => Create(world).ExecuteAsync(45, request, default);

        await act.Should().ThrowAsync<ValidationException>();
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: Exception Path 3 - 400 Validation Error when reason is empty or exceeds 1000 characters
    [Fact]
    public async Task EmptyReason_ThrowsValidationException()
    {
        var world = new ModerationTestContext();

        var request = new UpdateBlogStatusRequestDto
        {
            Status = "hidden",
            Reason = ""
        };

        var act = () => Create(world).ExecuteAsync(45, request, default);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ExcessivelyLongReason_ThrowsValidationException()
    {
        var world = new ModerationTestContext();

        var request = new UpdateBlogStatusRequestDto
        {
            Status = "hidden",
            Reason = new string('x', 1001)
        };

        var act = () => Create(world).ExecuteAsync(45, request, default);

        await act.Should().ThrowAsync<ValidationException>();
    }

    // TEST-06: Forbidden Path - Mod without BLOG_MODERATE permission throws ForbiddenException
    [Fact]
    public async Task MissingBlogModeratePermission_ThrowsForbiddenException()
    {
        var world = new ModerationTestContext();
        world.Accounts.Setup(x => x.GetEffectivePermissionsAsync(3, "MOD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["RESTAURANT_MODERATE"]);

        var request = new UpdateBlogStatusRequestDto
        {
            Status = "hidden",
            Reason = "Không đủ quyền"
        };

        var act = () => Create(world).ExecuteAsync(45, request, default);

        var ex = await act.Should().ThrowAsync<ForbiddenException>();
        ex.Which.ErrorCode.Should().Be("FORBIDDEN");
    }
}
