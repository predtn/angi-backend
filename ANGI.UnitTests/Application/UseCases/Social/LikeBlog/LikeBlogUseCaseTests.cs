using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Social.LikeBlog;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Social.LikeBlog;

/// <summary>Unit tests for BLOG-08 LikeBlogUseCase.</summary>
public sealed class LikeBlogUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
    private readonly Mock<IBlogRepository> _blogRepository = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly FixedTimeProvider _timeProvider = new(Now);

    public LikeBlogUseCaseTests()
    {
        _currentUserService.Setup(s => s.UserId).Returns(10);
    }

    private LikeBlogUseCase CreateUseCase() =>
        new(_blogRepository.Object, _currentUserService.Object, _unitOfWork.Object, _timeProvider);

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_ShouldAddLikeAndIncrementLikeCount()
    {
        // Arrange
        const long blogId = 45;
        const int userId = 10;
        var blog = new Blog
        {
            Id = blogId,
            AuthorId = 99,
            Title = "Đà Nẵng ẩm thực",
            Status = BlogStatus.Published,
            LikeCount = 5
        };

        _blogRepository.Setup(r => r.GetByIdAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);
        _blogRepository.Setup(r => r.HasUserLikedAsync(blogId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        BlogLike? addedLike = null;
        _blogRepository.Setup(r => r.AddLike(It.IsAny<BlogLike>()))
            .Callback<BlogLike>(like => addedLike = like);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(blogId, CancellationToken.None);

        // Assert
        result.Liked.Should().BeTrue();
        result.LikeCount.Should().Be(6);
        blog.LikeCount.Should().Be(6);

        addedLike.Should().NotBeNull();
        addedLike!.BlogId.Should().Be(blogId);
        addedLike.UserId.Should().Be(userId);
        addedLike.CreatedAt.Should().Be(Now);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAlreadyLiked_ShouldBeIdempotentAndNotIncrementCount()
    {
        // Arrange
        const long blogId = 45;
        const int userId = 10;
        var blog = new Blog
        {
            Id = blogId,
            AuthorId = 99,
            Title = "Đà Nẵng ẩm thực",
            Status = BlogStatus.Published,
            LikeCount = 5
        };

        _blogRepository.Setup(r => r.GetByIdAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);
        _blogRepository.Setup(r => r.HasUserLikedAsync(blogId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(blogId, CancellationToken.None);

        // Assert
        result.Liked.Should().BeTrue();
        result.LikeCount.Should().Be(5);
        blog.LikeCount.Should().Be(5);

        _blogRepository.Verify(r => r.AddLike(It.IsAny<BlogLike>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBlogDoesNotExist_ShouldThrowNotFoundException()
    {
        // Arrange
        const long nonExistentBlogId = 9999;
        _blogRepository.Setup(r => r.GetByIdAsync(nonExistentBlogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Blog?)null);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(nonExistentBlogId, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("BLOG_NOT_FOUND");
    }

    [Fact]
    public async Task ExecuteAsync_WhenBlogIsNotPublished_ShouldThrowNotFoundException()
    {
        // Arrange
        const long blogId = 45;
        var blog = new Blog
        {
            Id = blogId,
            AuthorId = 99,
            Title = "Blog bị ẩn",
            Status = BlogStatus.Hidden,
            LikeCount = 2
        };

        _blogRepository.Setup(r => r.GetByIdAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(blogId, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("BLOG_NOT_FOUND");
    }

    [Fact]
    public async Task ExecuteAsync_WhenUserNotAuthenticated_ShouldThrowUnauthorizedException()
    {
        // Arrange
        _currentUserService.Setup(s => s.UserId).Returns((int?)null);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(45, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<UnauthorizedException>();
        ex.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }
}
