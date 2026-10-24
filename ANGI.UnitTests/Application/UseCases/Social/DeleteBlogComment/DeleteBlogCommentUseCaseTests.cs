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

    // TEST-01 (Happy Path): Soft-deletes own comment and decrements blog comment count.
    [Fact]
    public async Task ExecuteAsync_HappyPath_ShouldSoftDeleteCommentAndDecrementBlogCount()
    {
        // Arrange
        const long blogId = 45;
        const long commentId = 302;
        const int authorId = 12;

        var blog = new Blog
        {
            Id = blogId,
            CommentCount = 5,
            Status = BlogStatus.Published
        };

        var comment = new BlogComment
        {
            Id = commentId,
            BlogId = blogId,
            UserId = authorId,
            Content = "Bình luận cần xóa",
            Blog = blog,
            CreatedAt = Now.AddHours(-1)
        };

        var commentRepo = new Mock<IBlogCommentRepository>();
        commentRepo.Setup(r => r.GetByIdAndBlogIdAsync(commentId, blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(comment);

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.UserId).Returns(authorId);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var useCase = new DeleteBlogCommentUseCase(
            commentRepo.Object,
            currentUser.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));

        // Act
        await useCase.ExecuteAsync(blogId, commentId, CancellationToken.None);

        // Assert
        comment.DeletedAt.Should().Be(Now);
        comment.UpdatedAt.Should().Be(Now);
        blog.CommentCount.Should().Be(4);
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
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

    // TEST-05: Comment count should not drop below zero when deleting.
    [Fact]
    public async Task ExecuteAsync_WhenCommentCountIsZero_ShouldNotDropBelowZero()
    {
        // Arrange
        const long blogId = 45;
        const long commentId = 302;
        const int authorId = 12;

        var blog = new Blog { Id = blogId, CommentCount = 0 };
        var comment = new BlogComment
        {
            Id = commentId,
            BlogId = blogId,
            UserId = authorId,
            Blog = blog
        };

        var commentRepo = new Mock<IBlogCommentRepository>();
        commentRepo.Setup(r => r.GetByIdAndBlogIdAsync(commentId, blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(comment);

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.UserId).Returns(authorId);

        var unitOfWork = new Mock<IUnitOfWork>();

        var useCase = new DeleteBlogCommentUseCase(
            commentRepo.Object,
            currentUser.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));

        // Act
        await useCase.ExecuteAsync(blogId, commentId, CancellationToken.None);

        // Assert
        blog.CommentCount.Should().Be(0);
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
