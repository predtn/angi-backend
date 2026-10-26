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
    private readonly Mock<IUnitOfWorkTransaction> _transaction = new();

    public UnlikeBlogUseCaseTests()
    {
        _currentUserService.Setup(s => s.UserId).Returns(10);
        _unitOfWork.Setup(u => u.BeginTransactionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_transaction.Object);
        _blogRepository.Setup(r => r.GetByIdAsync(45, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Blog { Id = 45, AuthorId = 99, Title = "Blog", Status = BlogStatus.Published, LikeCount = 6 });
    }

    private UnlikeBlogUseCase CreateUseCase() =>
        new(_blogRepository.Object, _currentUserService.Object, _unitOfWork.Object);

    // TEST-01: Removing an existing like subtracts 1 from like_count in SQL, in one transaction.
    /// <summary>Removing an existing like subtracts 1 from like_count in SQL, in one transaction.</summary>
    [Fact]
    public async Task ExecuteAsync_HappyPath_ShouldRemoveLikeAndDecrementLikeCount()
    {
        _blogRepository.Setup(r => r.TryRemoveLikeAsync(45, 10, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _blogRepository.Setup(r => r.AddToLikeCountAsync(45, -1, It.IsAny<CancellationToken>())).ReturnsAsync(5);

        var result = await CreateUseCase().ExecuteAsync(45, CancellationToken.None);

        result.Liked.Should().BeFalse();
        result.LikeCount.Should().Be(5);
        _transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Unliking a blog that is not liked (or losing a concurrent race) keeps the count.
    /// <summary>Unliking a blog that is not liked (or losing a concurrent race) keeps the count.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenNotLiked_ShouldBeIdempotentAndNotDecrement()
    {
        _blogRepository.Setup(r => r.TryRemoveLikeAsync(45, 10, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _blogRepository.Setup(r => r.AddToLikeCountAsync(45, 0, It.IsAny<CancellationToken>())).ReturnsAsync(6);

        var result = await CreateUseCase().ExecuteAsync(45, CancellationToken.None);

        result.Liked.Should().BeFalse();
        result.LikeCount.Should().Be(6);
        _blogRepository.Verify(r => r.AddToLikeCountAsync(45, -1, It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: A missing blog is BLOG_NOT_FOUND and writes nothing.
    /// <summary>A missing blog is BLOG_NOT_FOUND and writes nothing.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenBlogDoesNotExist_ShouldThrowNotFoundException()
    {
        var act = () => CreateUseCase().ExecuteAsync(999, CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("BLOG_NOT_FOUND");
        _blogRepository.Verify(r => r.TryRemoveLikeAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
