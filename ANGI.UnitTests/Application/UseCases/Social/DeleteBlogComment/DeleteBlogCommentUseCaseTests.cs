using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Social.DeleteBlogComment;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Social.DeleteBlogComment;

/// <summary>Unit tests for BLOG-11 (Delete Blog Comment).</summary>
public sealed class DeleteBlogCommentUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 14, 0, 0, DateTimeKind.Utc);

    // TEST-01 (Happy Path): Soft-deletes own comment; the repository also decrements comment_count, in one transaction.
    [Fact]
    public async Task ExecuteAsync_HappyPath_ShouldSoftDeleteCommentAndDecrementBlogCount()
    {
        var harness = CreateHarness(commentOwnerId: 12, currentUserId: 12, deleted: true);

        await harness.UseCase.ExecuteAsync(45, 302, CancellationToken.None);

        harness.Comments.Verify(r => r.TrySoftDeleteAsync(302, 45, Now, It.IsAny<CancellationToken>()), Times.Once);
        harness.Transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02 (Exception Path 2): User cố tình xóa bình luận của người khác (bắn 403 NOT_OWNER).
    [Fact]
    public async Task ExecuteAsync_WhenUserIsNotOwner_ShouldThrowForbiddenException()
    {
        // Arrange
        const long blogId = 45;
        const long commentId = 302;
        const int commentOwnerId = 12;
        const int otherUserId = 99;

        var comment = new BlogComment
        {
            Id = commentId,
            BlogId = blogId,
            UserId = commentOwnerId,
            Content = "Bình luận của người khác"
        };

        var commentRepo = new Mock<IBlogCommentRepository>();
        commentRepo.Setup(r => r.GetByIdAndBlogIdAsync(commentId, blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(comment);

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.UserId).Returns(otherUserId);

        var unitOfWork = new Mock<IUnitOfWork>();

        var useCase = new DeleteBlogCommentUseCase(
            commentRepo.Object,
            currentUser.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));

        // Act
        var act = () => useCase.ExecuteAsync(blogId, commentId, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<ForbiddenException>();
        ex.Which.ErrorCode.Should().Be("NOT_OWNER");
        comment.DeletedAt.Should().BeNull();
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Comment not found or does not belong to blog should throw NotFoundException (404 COMMENT_NOT_FOUND).
    [Fact]
    public async Task ExecuteAsync_CommentNotFound_ShouldThrowNotFoundException()
    {
        // Arrange
        const long blogId = 45;
        const long nonExistentCommentId = 999;

        var commentRepo = new Mock<IBlogCommentRepository>();
        commentRepo.Setup(r => r.GetByIdAndBlogIdAsync(nonExistentCommentId, blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BlogComment?)null);

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.UserId).Returns(12);

        var useCase = new DeleteBlogCommentUseCase(
            commentRepo.Object,
            currentUser.Object,
            Mock.Of<IUnitOfWork>(),
            new FixedTimeProvider(Now));

        // Act
        var act = () => useCase.ExecuteAsync(blogId, nonExistentCommentId, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("COMMENT_NOT_FOUND");
    }

    // TEST-04: Unauthenticated user should throw UnauthorizedException (401 UNAUTHORIZED).
    [Fact]
    public async Task ExecuteAsync_UnauthenticatedUser_ShouldThrowUnauthorizedException()
    {
        // Arrange
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.UserId).Returns((int?)null);

        var useCase = new DeleteBlogCommentUseCase(
            Mock.Of<IBlogCommentRepository>(),
            currentUser.Object,
            Mock.Of<IUnitOfWork>(),
            new FixedTimeProvider(Now));

        // Act
        var act = () => useCase.ExecuteAsync(45, 302, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<UnauthorizedException>();
        ex.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    // TEST-05: When a concurrent delete wins, this call still succeeds and decrements nothing (the repository skips it).
    [Fact]
    public async Task ExecuteAsync_WhenConcurrentDeleteWins_ShouldSucceedWithoutSecondDecrement()
    {
        var harness = CreateHarness(commentOwnerId: 12, currentUserId: 12, deleted: false);

        await harness.UseCase.ExecuteAsync(45, 302, CancellationToken.None);

        harness.Transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Harness CreateHarness(int commentOwnerId, int currentUserId, bool deleted)
    {
        var comments = new Mock<IBlogCommentRepository>();
        comments.Setup(r => r.GetByIdAndBlogIdAsync(302, 45, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BlogComment { Id = 302, BlogId = 45, UserId = commentOwnerId, Content = "Bình luận cần xóa" });
        comments.Setup(r => r.TrySoftDeleteAsync(302, 45, Now, It.IsAny<CancellationToken>())).ReturnsAsync(deleted);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.UserId).Returns(currentUserId);
        var transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.BeginTransactionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(transaction.Object);
        return new Harness(
            new DeleteBlogCommentUseCase(comments.Object, currentUser.Object, unitOfWork.Object, new FixedTimeProvider(Now)),
            comments,
            transaction);
    }

    private sealed record Harness(
        DeleteBlogCommentUseCase UseCase,
        Mock<IBlogCommentRepository> Comments,
        Mock<IUnitOfWorkTransaction> Transaction);

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
