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
    private readonly Mock<IUnitOfWorkTransaction> _transaction = new();
    private readonly FixedTimeProvider _timeProvider = new(Now);

    public LikeBlogUseCaseTests()
    {
        _currentUserService.Setup(s => s.UserId).Returns(10);
        _unitOfWork.Setup(u => u.BeginTransactionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_transaction.Object);
    }

    private LikeBlogUseCase CreateUseCase() =>
        new(_blogRepository.Object, _currentUserService.Object, _unitOfWork.Object, _timeProvider);

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private static Blog PublishedBlog() => new()
    {
        Id = 45,
        AuthorId = 99,
        Title = "Đà Nẵng ẩm thực",
        Status = BlogStatus.Published,
        LikeCount = 5
    };

    // TEST-01: A first like inserts the row and adds 1 to like_count in SQL, in one transaction.
    /// <summary>A first like inserts the row and adds 1 to like_count in SQL, in one transaction.</summary>
    [Fact]
    public async Task ExecuteAsync_HappyPath_ShouldAddLikeAndIncrementLikeCount()
    {
        _blogRepository.Setup(r => r.GetByIdAsync(45, It.IsAny<CancellationToken>())).ReturnsAsync(PublishedBlog());
        _blogRepository.Setup(r => r.TryAddLikeAsync(45, 10, Now, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _blogRepository.Setup(r => r.AddToLikeCountAsync(45, 1, It.IsAny<CancellationToken>())).ReturnsAsync(6);

        var result = await CreateUseCase().ExecuteAsync(45, CancellationToken.None);

        result.Liked.Should().BeTrue();
        result.LikeCount.Should().Be(6);
        _transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Liking again (or losing a concurrent race) keeps the count and still answers liked.
    /// <summary>Liking again (or losing a concurrent race) keeps the count and still answers liked.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenAlreadyLiked_ShouldBeIdempotentAndNotIncrementCount()
    {
        _blogRepository.Setup(r => r.GetByIdAsync(45, It.IsAny<CancellationToken>())).ReturnsAsync(PublishedBlog());
        _blogRepository.Setup(r => r.TryAddLikeAsync(45, 10, Now, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _blogRepository.Setup(r => r.AddToLikeCountAsync(45, 0, It.IsAny<CancellationToken>())).ReturnsAsync(5);

        var result = await CreateUseCase().ExecuteAsync(45, CancellationToken.None);

        result.Liked.Should().BeTrue();
        result.LikeCount.Should().Be(5);
        _blogRepository.Verify(r => r.AddToLikeCountAsync(45, 1, It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: A missing blog is BLOG_NOT_FOUND and writes nothing.
    /// <summary>A missing blog is BLOG_NOT_FOUND and writes nothing.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenBlogDoesNotExist_ShouldThrowNotFoundException()
    {
        var act = () => CreateUseCase().ExecuteAsync(999, CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("BLOG_NOT_FOUND");
        _blogRepository.Verify(r => r.TryAddLikeAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: A blog that is not published cannot be liked.
    /// <summary>A blog that is not published cannot be liked.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenBlogIsNotPublished_ShouldThrowNotFoundException()
    {
        var blog = PublishedBlog();
        blog.Status = BlogStatus.Hidden;
        _blogRepository.Setup(r => r.GetByIdAsync(45, It.IsAny<CancellationToken>())).ReturnsAsync(blog);

        var act = () => CreateUseCase().ExecuteAsync(45, CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("BLOG_NOT_FOUND");
        _blogRepository.Verify(r => r.TryAddLikeAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: An anonymous call is rejected before any read.
    /// <summary>An anonymous call is rejected before any read.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenUserNotAuthenticated_ShouldThrowUnauthorizedException()
    {
        _currentUserService.Setup(s => s.UserId).Returns((int?)null);

        var act = () => CreateUseCase().ExecuteAsync(45, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
        _blogRepository.Verify(r => r.GetByIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
