using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Social.UnlikeBlog;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Social.UnlikeBlog;

/// <summary>Unit tests for BLOG-09 UnlikeBlogUseCase.</summary>
public sealed class UnlikeBlogUseCaseTests
{
    private readonly Mock<IBlogRepository> _blogRepository = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public UnlikeBlogUseCaseTests()
    {
        _currentUserService.Setup(s => s.UserId).Returns(10);
    }

    private UnlikeBlogUseCase CreateUseCase() =>
        new(_blogRepository.Object, _currentUserService.Object, _unitOfWork.Object);

    [Fact]
    public async Task ExecuteAsync_HappyPath_ShouldRemoveLikeAndDecrementLikeCount()
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
        var existingLike = new BlogLike { BlogId = blogId, UserId = userId };

        _blogRepository.Setup(r => r.GetByIdAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);
        _blogRepository.Setup(r => r.GetLikeAsync(blogId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingLike);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(blogId, CancellationToken.None);

        // Assert
        result.Liked.Should().BeFalse();
        result.LikeCount.Should().Be(4);
        blog.LikeCount.Should().Be(4);

        _blogRepository.Verify(r => r.RemoveLike(existingLike), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNotLiked_ShouldBeIdempotentAndNotDecrement()
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
        _blogRepository.Setup(r => r.GetLikeAsync(blogId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BlogLike?)null);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(blogId, CancellationToken.None);

        // Assert
        result.Liked.Should().BeFalse();
        result.LikeCount.Should().Be(5);
        blog.LikeCount.Should().Be(5);

        _blogRepository.Verify(r => r.RemoveLike(It.IsAny<BlogLike>()), Times.Never);
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
}
