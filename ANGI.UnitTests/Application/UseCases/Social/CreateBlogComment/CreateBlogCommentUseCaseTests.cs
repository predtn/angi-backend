using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Social;
using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Social.CreateBlogComment;
using ANGI.Application.UseCases.Validators.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Social.CreateBlogComment;

/// <summary>Unit tests for BLOG-10 (Create Blog Comment).</summary>
public sealed class CreateBlogCommentUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    // TEST-01 (Happy Path): Creating comment on existing blog succeeds, increments comment count, and maps DTO.
    [Fact]
    public async Task ExecuteAsync_HappyPath_ShouldCreateCommentSuccessfully()
    {
        // Arrange
        const long blogId = 45;
        const int commenterId = 12;
        const int blogAuthorId = 99;

        var blog = new Blog
        {
            Id = blogId,
            AuthorId = blogAuthorId,
            Title = "Quán ngon Đà Nẵng",
            Status = BlogStatus.Published,
            CommentCount = 2
        };

        var user = new User
        {
            Id = commenterId,
            DisplayName = "Nguyễn An",
            AvatarMedia = new MediaFile { Url = "https://cdn.angi.vn/avatars/12.jpg" }
        };

        var commentRepo = new Mock<IBlogCommentRepository>();
        commentRepo.Setup(r => r.GetActiveBlogByIdAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);
        commentRepo.Setup(r => r.GetUserAsync(commenterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        BlogComment? addedComment = null;
        commentRepo.Setup(r => r.Add(It.IsAny<BlogComment>()))
            .Callback<BlogComment>(c =>
            {
                c.Id = 302;
                addedComment = c;
            });

        Domain.Entities.Notification? addedNotification = null;
        commentRepo.Setup(r => r.AddNotification(It.IsAny<Domain.Entities.Notification>()))
            .Callback<Domain.Entities.Notification>(n => addedNotification = n);

        var sanitizer = new Mock<IBlogContentSanitizer>();
        sanitizer.Setup(s => s.Sanitize(It.IsAny<string>()))
            .Returns<string>(c => c);

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.UserId).Returns(commenterId);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var useCase = new CreateBlogCommentUseCase(
            new CreateBlogCommentRequestDtoValidator(),
            commentRepo.Object,
            sanitizer.Object,
            currentUser.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));

        var request = new CreateBlogCommentRequestDto { Content = "Quán này ngon thật!" };

        // Act
        var result = await useCase.ExecuteAsync(blogId, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(302);
        result.BlogId.Should().Be(blogId);
        result.Content.Should().Be("Quán này ngon thật!");
        result.IsMine.Should().BeTrue();
        result.User.Id.Should().Be(commenterId);
        result.User.DisplayName.Should().Be("Nguyễn An");
        result.User.AvatarUrl.Should().Be("https://cdn.angi.vn/avatars/12.jpg");

        blog.CommentCount.Should().Be(3);
        addedComment.Should().NotBeNull();
        addedComment!.Content.Should().Be("Quán này ngon thật!");
        addedComment.Status.Should().Be(ContentStatus.Visible);

        addedNotification.Should().NotBeNull();
        addedNotification!.RecipientId.Should().Be(blogAuthorId);
        addedNotification.Type.Should().Be("BLOG_COMMENTED");

        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Commenting on own blog does not trigger a notification.
    [Fact]
    public async Task ExecuteAsync_WhenCommentingOnOwnBlog_ShouldNotSendNotification()
    {
        // Arrange
        const long blogId = 45;
        const int authorId = 12;

        var blog = new Blog
        {
            Id = blogId,
            AuthorId = authorId,
            Title = "Quán ngon Đà Nẵng",
            Status = BlogStatus.Published,
            CommentCount = 0
        };

        var user = new User { Id = authorId, DisplayName = "Nguyễn An" };

        var commentRepo = new Mock<IBlogCommentRepository>();
        commentRepo.Setup(r => r.GetActiveBlogByIdAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);
        commentRepo.Setup(r => r.GetUserAsync(authorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var sanitizer = new Mock<IBlogContentSanitizer>();
        sanitizer.Setup(s => s.Sanitize(It.IsAny<string>())).Returns<string>(c => c);

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.UserId).Returns(authorId);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var useCase = new CreateBlogCommentUseCase(
            new CreateBlogCommentRequestDtoValidator(),
            commentRepo.Object,
            sanitizer.Object,
            currentUser.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));

        var request = new CreateBlogCommentRequestDto { Content = "Tôi tự comment bài của mình." };

        // Act
        var result = await useCase.ExecuteAsync(blogId, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        commentRepo.Verify(r => r.AddNotification(It.IsAny<Domain.Entities.Notification>()), Times.Never);
        blog.CommentCount.Should().Be(1);
    }

    // TEST-03 (Exception Path 1): Truyền blogId không tồn tại (bắn 404 BLOG_NOT_FOUND).
    [Fact]
    public async Task ExecuteAsync_NonExistentBlogId_ShouldThrowNotFoundException()
    {
        // Arrange
        const long nonExistentBlogId = 999;
        var commentRepo = new Mock<IBlogCommentRepository>();
        commentRepo.Setup(r => r.GetActiveBlogByIdAsync(nonExistentBlogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Blog?)null);

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.UserId).Returns(12);

        var useCase = new CreateBlogCommentUseCase(
            new CreateBlogCommentRequestDtoValidator(),
            commentRepo.Object,
            Mock.Of<IBlogContentSanitizer>(),
            currentUser.Object,
            Mock.Of<IUnitOfWork>(),
            new FixedTimeProvider(Now));

        var request = new CreateBlogCommentRequestDto { Content = "Bình luận thử" };

        // Act
        var act = () => useCase.ExecuteAsync(nonExistentBlogId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("BLOG_NOT_FOUND");
    }

    // TEST-04: User not authenticated should throw UnauthorizedException (401).
    [Fact]
    public async Task ExecuteAsync_UnauthenticatedUser_ShouldThrowUnauthorizedException()
    {
        // Arrange
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.UserId).Returns((int?)null);

        var useCase = new CreateBlogCommentUseCase(
            new CreateBlogCommentRequestDtoValidator(),
            Mock.Of<IBlogCommentRepository>(),
            Mock.Of<IBlogContentSanitizer>(),
            currentUser.Object,
            Mock.Of<IUnitOfWork>(),
            new FixedTimeProvider(Now));

        var request = new CreateBlogCommentRequestDto { Content = "Nội dung" };

        // Act
        var act = () => useCase.ExecuteAsync(45, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<UnauthorizedException>();
        ex.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    // TEST-05: Sanitizer strips all content resulting in empty string should throw ValidationException (400).
    [Fact]
    public async Task ExecuteAsync_SanitizedContentEmpty_ShouldThrowValidationException()
    {
        // Arrange
        const long blogId = 45;
        const int userId = 12;

        var blog = new Blog { Id = blogId, Status = BlogStatus.Published };
        var user = new User { Id = userId, DisplayName = "Nguyễn An" };

        var commentRepo = new Mock<IBlogCommentRepository>();
        commentRepo.Setup(r => r.GetActiveBlogByIdAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);
        commentRepo.Setup(r => r.GetUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var sanitizer = new Mock<IBlogContentSanitizer>();
        sanitizer.Setup(s => s.Sanitize(It.IsAny<string>())).Returns(string.Empty);

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.UserId).Returns(userId);

        var useCase = new CreateBlogCommentUseCase(
            new CreateBlogCommentRequestDtoValidator(),
            commentRepo.Object,
            sanitizer.Object,
            currentUser.Object,
            Mock.Of<IUnitOfWork>(),
            new FixedTimeProvider(Now));

        var request = new CreateBlogCommentRequestDto { Content = "<script>alert('xss')</script>" };

        // Act
        var act = () => useCase.ExecuteAsync(blogId, request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
