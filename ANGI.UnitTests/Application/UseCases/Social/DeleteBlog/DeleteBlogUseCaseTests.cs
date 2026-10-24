using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Social.DeleteBlog;
using ANGI.Domain.Entities;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Social.DeleteBlog;

public sealed class DeleteBlogUseCaseTests
{
    // TEST-01: Soft-delete an owned blog at the injected UTC time and commit exactly once.
    /// <summary>Verifies the complete successful BLOG-07 transaction and persistence path.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOwnedBlog_ShouldSetDeletedAtAndCommit()
    {
        var now = new DateTimeOffset(2026, 10, 22, 15, 30, 0, TimeSpan.Zero);
        var blog = new Blog { Id = 42, AuthorId = 10, Title = "Du lịch Đà Nẵng", Content = "Nội dung" };
        var fixture = CreateFixture(blog, now, 10);
        using var cancellationSource = new CancellationTokenSource();

        await fixture.UseCase.ExecuteAsync(42, cancellationSource.Token);

        blog.DeletedAt.Should().Be(now.UtcDateTime);
        fixture.Repository.Verify(item => item.GetTrackedByIdAsync(42, cancellationSource.Token), Times.Once);
        fixture.UnitOfWork.Verify(item => item.SaveChangesAsync(cancellationSource.Token), Times.Once);
        fixture.Transaction.Verify(item => item.CommitAsync(cancellationSource.Token), Times.Once);
    }

    // TEST-02: Throw 404 BLOG_NOT_FOUND when the blog does not exist or has already been soft-deleted.
    /// <summary>Verifies the missing resource path rolls back without saving or committing.</summary>
    [Fact]
    public async Task ExecuteAsync_WithNonExistentBlog_ShouldThrowNotFoundWithoutSaving()
    {
        var fixture = CreateFixture(null, DateTimeOffset.UtcNow, 10);
        using var cancellationSource = new CancellationTokenSource();

        var act = () => fixture.UseCase.ExecuteAsync(404, cancellationSource.Token);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("BLOG_NOT_FOUND");
        exception.Which.Message.Should().Be("Không tìm thấy blog.");

        fixture.Repository.Verify(item => item.GetTrackedByIdAsync(404, cancellationSource.Token), Times.Once);
        fixture.UnitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        fixture.Transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Throw 403 NOT_OWNER when a traveler attempts to delete a blog authored by another user.
    /// <summary>Verifies ownership check enforces forbidden access and preserves the blog untouched.</summary>
    [Fact]
    public async Task ExecuteAsync_WithBlogOwnedByAnotherUser_ShouldThrowForbiddenWithoutSaving()
    {
        var blog = new Blog { Id = 42, AuthorId = 99, Title = "Bài của người khác", Content = "Nội dung" };
        var fixture = CreateFixture(blog, DateTimeOffset.UtcNow, 10);
        using var cancellationSource = new CancellationTokenSource();

        var act = () => fixture.UseCase.ExecuteAsync(42, cancellationSource.Token);

        var exception = await act.Should().ThrowAsync<ForbiddenException>();
        exception.Which.ErrorCode.Should().Be("NOT_OWNER");
        exception.Which.Message.Should().Be("Không phải chủ của tài nguyên.");

        blog.DeletedAt.Should().BeNull();
        fixture.Repository.Verify(item => item.GetTrackedByIdAsync(42, cancellationSource.Token), Times.Once);
        fixture.UnitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        fixture.Transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Reject an unauthenticated caller before opening a transaction or reading persistence.
    /// <summary>Verifies Application authorization remains enforced independently of the controller.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorizedWithoutPersistence()
    {
        var fixture = CreateFixture(new Blog(), DateTimeOffset.UtcNow, null);

        var act = () => fixture.UseCase.ExecuteAsync(42, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("UNAUTHORIZED");
        exception.Which.Message.Should().Be("Bạn cần đăng nhập để xóa blog.");

        fixture.Repository.Verify(item => item.GetTrackedByIdAsync(
            It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.UnitOfWork.Verify(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        fixture.UnitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Creates BLOG-07 fixture with deterministic identity, time, transaction, and repository mocks.</summary>
    private static Fixture CreateFixture(
        Blog? blog,
        DateTimeOffset now,
        int? userId)
    {
        var repository = new Mock<IBlogRepository>();
        repository.Setup(item => item.GetTrackedByIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(userId);

        var transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);

        var timeProvider = new Mock<TimeProvider>();
        timeProvider.Setup(item => item.GetUtcNow()).Returns(now);

        return new Fixture(
            new DeleteBlogUseCase(
                repository.Object,
                currentUser.Object,
                unitOfWork.Object,
                timeProvider.Object),
            repository,
            unitOfWork,
            transaction);
    }

    private sealed record Fixture(
        DeleteBlogUseCase UseCase,
        Mock<IBlogRepository> Repository,
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IUnitOfWorkTransaction> Transaction);
}
