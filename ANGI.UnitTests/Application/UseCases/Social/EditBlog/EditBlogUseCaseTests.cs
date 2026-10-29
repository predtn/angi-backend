using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Social;
using ANGI.Application.Common.Models.Social;
using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Social.EditBlog;
using ANGI.Application.UseCases.Validators.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Social.EditBlog;

/// <summary>Business-rule and boundary tests for BLOG-06.</summary>
public sealed class EditBlogUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    // TEST-01: Happy Path - Author successfully updates blog details, media, and refreshed roadmap snapshot.
    [Fact]
    public async Task ExecuteAsync_WithValidUpdatesByAuthor_ShouldUpdateBlogAndReturnDetail()
    {
        var existingBlog = CreateExistingBlog(authorId: 12, roadmapId: 88);
        var blogRepository = new Mock<IBlogRepository>();
        blogRepository.Setup(r => r.GetTrackedByIdAsync(45, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingBlog);
        blogRepository.Setup(r => r.SlugExistsAsync(It.IsAny<string>(), 45, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        blogRepository.Setup(r => r.GetOwnedRoadmapForSnapshotAsync(88, 12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoadmapSource());
        blogRepository.Setup(r => r.GetAuthorAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Author());

        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(m => m.GetOwnedByIdsAsync(
                12,
                It.Is<IReadOnlyCollection<long>>(ids => ids.Contains(9400) || ids.Contains(9401)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MediaFile { Id = 9400, UploaderId = 12, Url = "https://cdn.test/cover-new.jpg" },
                new MediaFile { Id = 9401, UploaderId = 12, Url = "https://cdn.test/image-new.jpg" }
            ]);

        var sanitizer = new Mock<IBlogContentSanitizer>();
        sanitizer.Setup(s => s.Sanitize("<p>New safe content</p><script>alert(1)</script>"))
            .Returns("<p>New safe content</p>");

        var unitOfWork = UnitOfWork(out var transaction);
        var useCase = CreateUseCase(blogRepository, mediaRepository, sanitizer, unitOfWork);

        var request = new EditBlogRequestDto
        {
            Title = "Hành trình Đà Nẵng mới",
            Content = "<p>New safe content</p><script>alert(1)</script>",
            CoverMediaId = 9400,
            ImageMediaIds = [9401],
            RefreshRoadmapSnapshot = true
        };

        var result = await useCase.ExecuteAsync(45, request, CancellationToken.None);

        result.Id.Should().Be(45);
        result.Title.Should().Be("Hành trình Đà Nẵng mới");
        result.Slug.Should().Be("hanh-trinh-da-nang-moi");
        result.Content.Should().Be("<p>New safe content</p>");
        result.CoverUrl.Should().Be("https://cdn.test/cover-new.jpg");
        result.Images.Should().Equal("https://cdn.test/image-new.jpg");
        result.Author.Id.Should().Be(12);
        result.HasRoadmap.Should().BeTrue();
        result.RoadmapId.Should().Be(88);
        result.RoadmapSnapshot.Should().NotBeNull();
        result.RoadmapSnapshot!.TotalCost.Should().Be(125000);
        result.IsMine.Should().BeTrue();

        existingBlog.Title.Should().Be("Hành trình Đà Nẵng mới");
        existingBlog.Slug.Should().Be("hanh-trinh-da-nang-moi");
        existingBlog.CoverMediaId.Should().Be(9400);
        existingBlog.Images.Should().ContainSingle(img => img.MediaId == 9401 && img.SortOrder == 0);

        unitOfWork.Verify(w => w.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Exception Path 1 - Throws 404 BLOG_NOT_FOUND when blog does not exist.
    [Fact]
    public async Task ExecuteAsync_WhenBlogDoesNotExist_ShouldThrowNotFoundException()
    {
        var blogRepository = new Mock<IBlogRepository>();
        blogRepository.Setup(r => r.GetTrackedByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Blog?)null);

        var useCase = CreateUseCase(blogRepository);

        var act = () => useCase.ExecuteAsync(999, new EditBlogRequestDto { Title = "Tiêu đề mới" }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("BLOG_NOT_FOUND");
    }

    // TEST-03: Exception Path 2 - Throws 403 NOT_OWNER when user is not the author.
    [Fact]
    public async Task ExecuteAsync_WhenUserIsNotAuthor_ShouldThrowForbiddenException()
    {
        var foreignBlog = CreateExistingBlog(authorId: 99); // Author is 99, caller is 12
        var blogRepository = new Mock<IBlogRepository>();
        blogRepository.Setup(r => r.GetTrackedByIdAsync(45, It.IsAny<CancellationToken>()))
            .ReturnsAsync(foreignBlog);

        var useCase = CreateUseCase(blogRepository);

        var act = () => useCase.ExecuteAsync(45, new EditBlogRequestDto { Title = "Tiêu đề mới" }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ForbiddenException>();
        ex.Which.ErrorCode.Should().Be("NOT_OWNER");
    }

    // TEST-04: Exception Path 3 - Throws 409 BLOG_REMOVED when blog status is Removed.
    [Fact]
    public async Task ExecuteAsync_WhenBlogStatusIsRemoved_ShouldThrowConflictException()
    {
        var removedBlog = CreateExistingBlog(authorId: 12);
        removedBlog.Status = BlogStatus.Removed;

        var blogRepository = new Mock<IBlogRepository>();
        blogRepository.Setup(r => r.GetTrackedByIdAsync(45, It.IsAny<CancellationToken>()))
            .ReturnsAsync(removedBlog);

        var useCase = CreateUseCase(blogRepository);

        var act = () => useCase.ExecuteAsync(45, new EditBlogRequestDto { Title = "Tiêu đề mới" }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ConflictException>();
        ex.Which.ErrorCode.Should().Be("BLOG_REMOVED");
    }

    // TEST-05: Exception Path 4 - Throws 401 UNAUTHORIZED when caller has no authenticated user ID.
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(s => s.UserId).Returns((int?)null);

        var useCase = CreateUseCase(currentUser: currentUser);

        var act = () => useCase.ExecuteAsync(45, new EditBlogRequestDto { Title = "Tiêu đề mới" }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<UnauthorizedException>();
        ex.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    // TEST-06: Exception Path 5 - Throws 400 ValidationException when media does not belong to the user.
    [Fact]
    public async Task ExecuteAsync_WhenMediaNotOwned_ShouldThrowValidationException()
    {
        var blog = CreateExistingBlog(authorId: 12);
        var blogRepository = new Mock<IBlogRepository>();
        blogRepository.Setup(r => r.GetTrackedByIdAsync(45, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);

        var mediaRepository = new Mock<IMediaRepository>();
        // Return empty: media 9999 is not owned by user 12
        mediaRepository.Setup(m => m.GetOwnedByIdsAsync(12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var useCase = CreateUseCase(blogRepository, mediaRepository);

        var act = () => useCase.ExecuteAsync(45, new EditBlogRequestDto { CoverMediaId = 9999 }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().ContainSingle(e => e.PropertyName == "CoverMediaId");
    }

    // TEST-07: Partial Update - Setting coverMediaId to null removes the cover image without touching other fields.
    [Fact]
    public async Task ExecuteAsync_WithCoverMediaIdNull_ShouldRemoveCover()
    {
        var blog = CreateExistingBlog(authorId: 12);
        blog.CoverMediaId = 8888;
        blog.CoverMedia = new MediaFile { Id = 8888, Url = "https://cdn.test/old-cover.jpg" };

        var blogRepository = new Mock<IBlogRepository>();
        blogRepository.Setup(r => r.GetTrackedByIdAsync(45, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);
        blogRepository.Setup(r => r.GetAuthorAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Author());

        var unitOfWork = UnitOfWork(out var transaction);
        var useCase = CreateUseCase(blogRepository, unitOfWork: unitOfWork);

        var request = new EditBlogRequestDto { CoverMediaId = null };

        var result = await useCase.ExecuteAsync(45, request, CancellationToken.None);

        blog.CoverMediaId.Should().BeNull();
        result.CoverUrl.Should().BeNull();
        unitOfWork.Verify(w => w.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-08: Content sanitization security check - Rejects content whose entire body is stripped by sanitizer.
    [Fact]
    public async Task ExecuteAsync_WhenSanitizedContentIsEmpty_ShouldThrowValidationException()
    {
        var blog = CreateExistingBlog(authorId: 12);
        var blogRepository = new Mock<IBlogRepository>();
        blogRepository.Setup(r => r.GetTrackedByIdAsync(45, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);

        var sanitizer = new Mock<IBlogContentSanitizer>();
        sanitizer.Setup(s => s.Sanitize("<script>bad()</script>")).Returns(string.Empty);

        var useCase = CreateUseCase(blogRepository, sanitizer: sanitizer);

        var act = () => useCase.ExecuteAsync(45, new EditBlogRequestDto { Content = "<script>bad()</script>" }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().ContainSingle(e => e.PropertyName == "Content");
    }

    private static Blog CreateExistingBlog(int authorId, long? roadmapId = null) => new()
    {
        Id = 45,
        AuthorId = authorId,
        Author = Author(),
        Title = "Bài viết cũ",
        Slug = "bai-viet-cu",
        Content = "<p>Nội dung cũ</p>",
        Status = BlogStatus.Published,
        RoadmapId = roadmapId,
        RoadmapSnapshot = roadmapId.HasValue ? "{}" : null,
        PublishedAt = Now.AddDays(-1),
        CreatedAt = Now.AddDays(-1),
        UpdatedAt = Now.AddDays(-1),
        Images = new List<BlogImage>()
    };

    private static User Author() => new()
    {
        Id = 12,
        DisplayName = "Nguyễn An",
        AvatarMedia = new MediaFile { Id = 9, Url = "https://cdn.test/avatar.jpg" }
    };

    private static RoadmapSnapshotSource RoadmapSource() => new()
    {
        RoadmapId = 88,
        Title = "Hai ngày ăn ngon",
        StartDate = new DateOnly(2026, 10, 20),
        NumDays = 2,
        Days =
        [
            new RoadmapSnapshotDaySource
            {
                DayNumber = 1,
                Items =
                [
                    new RoadmapSnapshotItemSource
                    {
                        DishId = 105,
                        MealSlot = MealSlot.Breakfast,
                        DishName = "Bún bò",
                        Price = 45000,
                        RestaurantId = 7,
                        RestaurantName = "Quán ngon",
                        AddressLine = "1 Hải Châu",
                        ProvinceName = "Đà Nẵng"
                    },
                    new RoadmapSnapshotItemSource
                    {
                        DishId = 106,
                        MealSlot = MealSlot.Dinner,
                        DishName = "Mì Quảng",
                        Price = 80000,
                        RestaurantId = 7,
                        RestaurantName = "Quán ngon",
                        AddressLine = "1 Hải Châu",
                        ProvinceName = "Đà Nẵng"
                    }
                ]
            }
        ]
    };

    private static Mock<IUnitOfWork> UnitOfWork(out Mock<IUnitOfWorkTransaction> transaction)
    {
        transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        return unitOfWork;
    }

    private static EditBlogUseCase CreateUseCase(
        Mock<IBlogRepository>? blogRepository = null,
        Mock<IMediaRepository>? mediaRepository = null,
        Mock<IBlogContentSanitizer>? sanitizer = null,
        Mock<IUnitOfWork>? unitOfWork = null,
        Mock<ICurrentUserService>? currentUser = null)
    {
        blogRepository ??= new Mock<IBlogRepository>();
        mediaRepository ??= new Mock<IMediaRepository>();
        if (sanitizer is null)
        {
            sanitizer = new Mock<IBlogContentSanitizer>();
            sanitizer.Setup(service => service.Sanitize(It.IsAny<string>())).Returns<string>(content => content);
        }
        unitOfWork ??= UnitOfWork(out _);
        if (currentUser is null)
        {
            currentUser = new Mock<ICurrentUserService>();
            currentUser.SetupGet(service => service.UserId).Returns(12);
        }

        return new EditBlogUseCase(
            new EditBlogRequestDtoValidator(),
            blogRepository.Object,
            mediaRepository.Object,
            sanitizer.Object,
            currentUser.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
