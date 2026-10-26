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

/// <summary>Unit tests for BLOG-10 CreateBlogCommentUseCase.</summary>
public sealed class CreateBlogCommentUseCaseTests
{
    private readonly IValidator<CreateBlogCommentRequestDto> _validator = new CreateBlogCommentRequestDtoValidator();
    private readonly Mock<IBlogRepository> _blogRepository = new();
    private readonly Mock<IBlogContentSanitizer> _contentSanitizer = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public CreateBlogCommentUseCaseTests()
    {
        _currentUserService.Setup(s => s.UserId).Returns(12);
        _contentSanitizer.Setup(s => s.Sanitize(It.IsAny<string>()))
            .Returns<string>(input => input);
    }

    private CreateBlogCommentUseCase CreateUseCase() =>
        new(_validator, _blogRepository.Object, _contentSanitizer.Object, _currentUserService.Object, _unitOfWork.Object);

    [Fact]
    public async Task ExecuteAsync_HappyPath_ShouldCreateCommentAndIncrementBlogCommentCount()
    {
        // Arrange
        const long blogId = 45;
        const int userId = 12;
        var authorUser = new User
        {
            Id = userId,
            DisplayName = "Nguyễn An",
            AvatarMedia = new MediaFile { Url = "https://cdn.angi.vn/avatar.jpg" }
        };
        var blog = new Blog
        {
            Id = blogId,
            AuthorId = 99,
            Title = "Quán ăn ngon",
            Status = BlogStatus.Published,
            CommentCount = 3
        };
        var request = new CreateBlogCommentRequestDto
        {
            Content = "Quán này ngon thật!"
        };

        _blogRepository.Setup(r => r.GetAuthorAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(authorUser);
        _blogRepository.Setup(r => r.GetByIdAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);

        BlogComment? addedComment = null;
        _blogRepository.Setup(r => r.AddComment(It.IsAny<BlogComment>()))
            .Callback<BlogComment>(c => addedComment = c);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(blogId, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.BlogId.Should().Be(blogId);
        result.Content.Should().Be("Quán này ngon thật!");
        result.IsMine.Should().BeTrue();
        result.User.Id.Should().Be(userId);
        result.User.DisplayName.Should().Be("Nguyễn An");
        result.User.AvatarUrl.Should().Be("https://cdn.angi.vn/avatar.jpg");

        blog.CommentCount.Should().Be(4);
        addedComment.Should().NotBeNull();
        addedComment!.BlogId.Should().Be(blogId);
        addedComment.UserId.Should().Be(userId);
        addedComment.Content.Should().Be("Quán này ngon thật!");
        addedComment.Status.Should().Be(ContentStatus.Visible);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBlogDoesNotExist_ShouldThrowNotFoundException()
    {
        // Arrange
        const long nonExistentBlogId = 9999;
        const int userId = 12;
        var authorUser = new User { Id = userId, DisplayName = "Nguyễn An" };

        _blogRepository.Setup(r => r.GetAuthorAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(authorUser);
        _blogRepository.Setup(r => r.GetByIdAsync(nonExistentBlogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Blog?)null);

        var request = new CreateBlogCommentRequestDto { Content = "Bình luận thử" };
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(nonExistentBlogId, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("BLOG_NOT_FOUND");
    }

    [Fact]
    public async Task ExecuteAsync_WhenContentIsEmpty_ShouldThrowValidationException()
    {
        // Arrange
        var request = new CreateBlogCommentRequestDto { Content = "" };
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(45, request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ExecuteAsync_WhenUserNotAuthenticated_ShouldThrowUnauthorizedException()
    {
        // Arrange
        _currentUserService.Setup(s => s.UserId).Returns((int?)null);
        var request = new CreateBlogCommentRequestDto { Content = "Bình luận" };
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(45, request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<UnauthorizedException>();
        ex.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    [Fact]
    public async Task ExecuteAsync_OnAnotherTravelersBlog_ShouldNotifyTheAuthor()
    {
        const long blogId = 45;
        var blog = new Blog { Id = blogId, AuthorId = 99, Title = "Quán ăn ngon", Status = BlogStatus.Published };
        _blogRepository.Setup(r => r.GetAuthorAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = 12, DisplayName = "Nguyễn An" });
        _blogRepository.Setup(r => r.GetByIdAsync(blogId, It.IsAny<CancellationToken>())).ReturnsAsync(blog);
        ANGI.Domain.Entities.Notification? notification = null;
        _blogRepository.Setup(r => r.AddNotification(It.IsAny<ANGI.Domain.Entities.Notification>()))
            .Callback<ANGI.Domain.Entities.Notification>(value => notification = value);
        var longContent = new string('a', 60);

        await CreateUseCase().ExecuteAsync(blogId, new CreateBlogCommentRequestDto { Content = longContent }, CancellationToken.None);

        notification.Should().NotBeNull();
        notification!.RecipientId.Should().Be(99);
        notification.Type.Should().Be("BLOG_COMMENTED");
        notification.Title.Should().Be("Có bình luận mới");
        notification.Body.Should().Be($"Nguyễn An: \"{new string('a', 47)}...\"");
        notification.Data.Should().Be("{\"blog_id\":45}");
    }

    [Fact]
    public async Task ExecuteAsync_OnOwnBlog_ShouldNotNotify()
    {
        const long blogId = 45;
        var blog = new Blog { Id = blogId, AuthorId = 12, Title = "Blog của tôi", Status = BlogStatus.Published };
        _blogRepository.Setup(r => r.GetAuthorAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = 12, DisplayName = "Nguyễn An" });
        _blogRepository.Setup(r => r.GetByIdAsync(blogId, It.IsAny<CancellationToken>())).ReturnsAsync(blog);

        await CreateUseCase().ExecuteAsync(blogId, new CreateBlogCommentRequestDto { Content = "Cảm ơn mọi người" }, CancellationToken.None);

        _blogRepository.Verify(r => r.AddNotification(It.IsAny<ANGI.Domain.Entities.Notification>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
