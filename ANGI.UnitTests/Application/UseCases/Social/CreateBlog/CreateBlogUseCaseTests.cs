using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Social;
using ANGI.Application.DTOs.Social;
using ANGI.Application.Common.Models.Social;
using ANGI.Application.UseCases.Social.CreateBlog;
using ANGI.Application.UseCases.Validators.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Social.CreateBlog;

/// <summary>Business-rule and boundary tests for BLOG-04.</summary>
public sealed class CreateBlogUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 3, 30, 0, DateTimeKind.Utc);

    // TEST-01: Sanitize, snapshot, order, persist, and map every BLOG-04 field atomically.
    /// <summary>Verifies the complete share-roadmap success path, including slug collision handling.</summary>
    [Fact]
    public async Task ExecuteAsync_WithRoadmapAndMedia_ShouldCreateCompletePublishedBlog()
    {
        // STEP-01: Declare added to capture a call argument and 1 more; mock IBlogRepository and 2 more; stub blogRepository.GetAuthorAsync and 6 more.
        var blogRepository = new Mock<IBlogRepository>();
        blogRepository.Setup(repository => repository.GetAuthorAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Author());
        blogRepository.Setup(repository => repository.GetOwnedRoadmapForSnapshotAsync(88, 12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoadmapSource());
        blogRepository.SetupSequence(repository => repository.SlugExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);
        Blog? added = null;
        blogRepository.Setup(repository => repository.Add(It.IsAny<Blog>()))
            .Callback<Blog>(blog => added = blog);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(repository => repository.GetOwnedByIdsAsync(
                12,
                It.Is<IReadOnlyCollection<long>>(ids => ids.ToHashSet().SetEquals(new long[] { 9400, 9401 })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MediaFile { Id = 9400, UploaderId = 12, Url = "https://cdn.test/cover.jpg" },
                new MediaFile { Id = 9401, UploaderId = 12, Url = "https://cdn.test/image.jpg" }
            ]);
        var sanitizer = new Mock<IBlogContentSanitizer>();
        sanitizer.Setup(service => service.Sanitize("<p>Safe</p><script>bad()</script>"))
            .Returns("<p>Safe</p>");
        var unitOfWork = UnitOfWork(out var transaction);
        unitOfWork.Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                added!.Id = 45;
                added.CreatedAt = Now;
                added.UpdatedAt = Now;
            })
            .ReturnsAsync(3);
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(blogRepository, mediaRepository, sanitizer, unitOfWork);

        // STEP-03: Run useCase.ExecuteAsync with CreateBlogRequestDto { Title, Content, CoverMediaId, ... } and keep result.
        var result = await useCase.ExecuteAsync(new CreateBlogRequestDto
        {
            Title = "  Hành trình Đà Nẵng  ",
            Content = "  <p>Safe</p><script>bad()</script>  ",
            CoverMediaId = 9400,
            ImageMediaIds = [9401],
            RoadmapId = 88
        }, CancellationToken.None);

        // STEP-04: Assert result.Id is 45, result.Title is the expected text, result.Slug is "hanh-trinh-da-nang-2", result.Content is "<p>Safe</p>" and 15 more.
        result.Id.Should().Be(45);
        result.Title.Should().Be("Hành trình Đà Nẵng");
        result.Slug.Should().Be("hanh-trinh-da-nang-2");
        result.Content.Should().Be("<p>Safe</p>");
        result.CoverUrl.Should().Be("https://cdn.test/cover.jpg");
        result.Images.Should().Equal("https://cdn.test/image.jpg");
        result.Author.Id.Should().Be(12);
        result.Author.AvatarUrl.Should().Be("https://cdn.test/avatar.jpg");
        result.HasRoadmap.Should().BeTrue();
        result.RoadmapId.Should().Be(88);
        result.RoadmapSnapshot!.CapturedAt.Should().Be(Now);
        result.RoadmapSnapshot.TotalCost.Should().Be(125000);
        result.RoadmapSnapshot.Days.Select(day => day.DayNumber).Should().Equal(1, 2);
        result.RoadmapSnapshot.Days[0].Items.Select(item => item.MealSlot).Should().Equal("breakfast", "dinner");
        result.RoadmapSnapshot.Days[0].Items[0].Address.Should().Be("1 Hải Châu, Phường Hải Châu, Đà Nẵng");
        result.IsMine.Should().BeTrue();
        result.LikedByMe.Should().BeFalse();
        result.PublishedAt.Should().Be(Now);
        result.UpdatedAt.Should().Be(Now);
        // STEP-05: Assert added.Status is BlogStatus.Published and added.Images has exactly one matching item.
        added!.Status.Should().Be(BlogStatus.Published);
        added.Images.Should().ContainSingle(image => image.MediaId == 9401 && image.SortOrder == 0);
        // STEP-06: Read snapshotJson from the response; assert RootElement.days[0].items[0].mealSlot is "breakfast".
        using var snapshotJson = JsonDocument.Parse(added.RoadmapSnapshot!);
        snapshotJson.RootElement.GetProperty("days")[0].GetProperty("items")[0]
            .GetProperty("mealSlot").GetString().Should().Be("breakfast");
        // STEP-07: Verify SaveChangesAsync is called once and CommitAsync is called once.
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Publish a text-only blog without querying optional media or roadmap dependencies.
    /// <summary>Verifies all nullable BLOG-04 fields remain null or empty in the response and entity.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutOptionalReferences_ShouldCreatePlainBlog()
    {
        // STEP-01: Get blogRepository from BaseBlogRepository.
        var blogRepository = BaseBlogRepository();
        // STEP-02: Declare added to capture a call argument and 1 more; mock IMediaRepository and 1 more; stub blogRepository.Add and 2 more.
        Blog? added = null;
        blogRepository.Setup(repository => repository.Add(It.IsAny<Blog>())).Callback<Blog>(blog => added = blog);
        var mediaRepository = new Mock<IMediaRepository>();
        var sanitizer = new Mock<IBlogContentSanitizer>();
        sanitizer.Setup(service => service.Sanitize("Nội dung")).Returns("Nội dung");
        var unitOfWork = UnitOfWork(out _);
        unitOfWork.Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => added!.UpdatedAt = Now)
            .ReturnsAsync(1);

        // STEP-03: Run CreateUseCase().ExecuteAsync with CreateBlogRequestDto { Title = the expected text, Content = the expected text } and keep result.
        var result = await CreateUseCase(blogRepository, mediaRepository, sanitizer, unitOfWork)
            .ExecuteAsync(new CreateBlogRequestDto { Title = "Bài mới", Content = "Nội dung" }, CancellationToken.None);

        // STEP-04: Assert result.HasRoadmap is false, result.RoadmapId is null, result.RoadmapSnapshot is null, result.CoverUrl is null and 1 more.
        result.HasRoadmap.Should().BeFalse();
        result.RoadmapId.Should().BeNull();
        result.RoadmapSnapshot.Should().BeNull();
        result.CoverUrl.Should().BeNull();
        result.Images.Should().BeEmpty();
        // STEP-05: Verify GetOwnedByIdsAsync is never called and GetOwnedRoadmapForSnapshotAsync is never called.
        mediaRepository.Verify(repository => repository.GetOwnedByIdsAsync(
            It.IsAny<int>(), It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()), Times.Never);
        blogRepository.Verify(repository => repository.GetOwnedRoadmapForSnapshotAsync(
            It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Hide missing and foreign roadmap states behind ROADMAP_NOT_FOUND without saving.
    /// <summary>Verifies the only documented BLOG-04 resource error and ownership boundary.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenOwnedRoadmapIsUnavailable_ShouldThrowRoadmapNotFound()
    {
        // STEP-01: Get blogRepository from BaseBlogRepository.
        var blogRepository = BaseBlogRepository();
        // STEP-02: Stub blogRepository.GetOwnedRoadmapForSnapshotAsync.
        blogRepository.Setup(repository => repository.GetOwnedRoadmapForSnapshotAsync(88, 12, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RoadmapSnapshotSource?)null);
        // STEP-03: Get unitOfWork from UnitOfWork.
        var unitOfWork = UnitOfWork(out _);

        // STEP-04: Wrap CreateUseCase().ExecuteAsync in act so its outcome can be asserted.
        var act = () => CreateUseCase(blogRepository, unitOfWork: unitOfWork)
            .ExecuteAsync(ValidRequest(roadmapId: 88), CancellationToken.None);

        // STEP-05: Assert act throws NotFoundException and exception.ErrorCode is "ROADMAP_NOT_FOUND".
        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("ROADMAP_NOT_FOUND");
        // STEP-06: Verify Add is never called and SaveChangesAsync is never called.
        blogRepository.Verify(repository => repository.Add(It.IsAny<Blog>()), Times.Never);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Reject foreign cover and inline media on their exact fields before roadmap or persistence work.
    /// <summary>Verifies every media-ownership failure is returned as VALIDATION_FAILED data.</summary>
    [Fact]
    public async Task ExecuteAsync_WithMissingOwnedMedia_ShouldReportBothFields()
    {
        // STEP-01: Get blogRepository from BaseBlogRepository.
        var blogRepository = BaseBlogRepository();
        // STEP-02: Mock IMediaRepository; stub mediaRepository.GetOwnedByIdsAsync.
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(repository => repository.GetOwnedByIdsAsync(
                12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        // STEP-03: Get unitOfWork from UnitOfWork, create request through ValidRequest, set request.CoverMediaId to 10 and set request.ImageMediaIds to a test items.
        var unitOfWork = UnitOfWork(out _);
        var request = ValidRequest();
        request.CoverMediaId = 10;
        request.ImageMediaIds = [11];

        // STEP-04: Wrap CreateUseCase().ExecuteAsync with request in act so its outcome can be asserted.
        var act = () => CreateUseCase(blogRepository, mediaRepository, unitOfWork: unitOfWork)
            .ExecuteAsync(request, CancellationToken.None);

        // STEP-05: Assert act throws ValidationException and exception.Errors.Select(filtered) is equivalent to "CoverMediaId".
        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.Errors.Select(error => error.PropertyName)
            .Should().BeEquivalentTo("CoverMediaId", "ImageMediaIds");
        // STEP-06: Verify Add is never called.
        blogRepository.Verify(repository => repository.Add(It.IsAny<Blog>()), Times.Never);
    }

    // TEST-05: Reject content reduced to no safe text by sanitization.
    /// <summary>Verifies script-only input cannot become a published empty article.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenSanitizerRemovesAllContent_ShouldRejectContent()
    {
        // STEP-01: Get blogRepository from BaseBlogRepository.
        var blogRepository = BaseBlogRepository();
        // STEP-02: Mock IBlogContentSanitizer; stub sanitizer.Sanitize.
        var sanitizer = new Mock<IBlogContentSanitizer>();
        sanitizer.Setup(service => service.Sanitize(It.IsAny<string>())).Returns(string.Empty);
        // STEP-03: Get unitOfWork from UnitOfWork.
        var unitOfWork = UnitOfWork(out _);

        // STEP-04: Wrap CreateUseCase().ExecuteAsync in act so its outcome can be asserted.
        var act = () => CreateUseCase(blogRepository, sanitizer: sanitizer, unitOfWork: unitOfWork)
            .ExecuteAsync(ValidRequest(content: "<script>bad()</script>"), CancellationToken.None);

        // STEP-05: Assert act throws ValidationException and exception.Errors has exactly one matching item.
        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.Errors.Should().ContainSingle(error => error.PropertyName == "Content");
        // STEP-06: Verify SaveChangesAsync is never called.
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-06: Run FluentValidation before identity, transactions, repositories, and sanitizer calls.
    /// <summary>Verifies the project-wide validation-first invariant for BLOG-04.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidRequest_ShouldStopAtValidation()
    {
        // STEP-01: Mock IBlogRepository and IUnitOfWork.
        var blogRepository = new Mock<IBlogRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();

        // STEP-02: Wrap CreateUseCase().ExecuteAsync with an empty CreateBlogRequestDto in act so its outcome can be asserted.
        var act = () => CreateUseCase(blogRepository, unitOfWork: unitOfWork)
            .ExecuteAsync(new CreateBlogRequestDto(), CancellationToken.None);

        // STEP-03: Assert act throws ValidationException.
        await act.Should().ThrowAsync<ValidationException>();
        // STEP-04: Verify BeginTransactionAsync is never called and blogRepository receives no other calls.
        unitOfWork.Verify(work => work.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        blogRepository.VerifyNoOtherCalls();
    }

    // TEST-07: Reject a direct unauthenticated use-case invocation independently of controller authorization.
    /// <summary>Verifies Application does not trust the HTTP authorization boundary alone.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        // STEP-01: Mock ICurrentUserService.
        var currentUser = new Mock<ICurrentUserService>();

        // STEP-02: Wrap CreateUseCase().ExecuteAsync in act so its outcome can be asserted.
        var act = () => CreateUseCase(currentUser: currentUser)
            .ExecuteAsync(ValidRequest(), CancellationToken.None);

        // STEP-03: Assert act throws UnauthorizedException and exception.ErrorCode is "UNAUTHORIZED".
        var exception = await act.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    /// <summary>Creates a repository with the authenticated author and a free base slug.</summary>
    private static Mock<IBlogRepository> BaseBlogRepository()
    {
        var repository = new Mock<IBlogRepository>();
        repository.Setup(item => item.GetAuthorAsync(12, It.IsAny<CancellationToken>())).ReturnsAsync(Author());
        repository.Setup(item => item.SlugExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        return repository;
    }

    /// <summary>Creates the author graph used by complete response-mapping assertions.</summary>
    private static User Author() => new()
    {
        Id = 12,
        DisplayName = "Nguyễn An",
        AvatarMedia = new MediaFile { Id = 9, Url = "https://cdn.test/avatar.jpg" }
    };

    /// <summary>Creates a two-day roadmap with deliberately unordered meals and current relational values.</summary>
    private static RoadmapSnapshotSource RoadmapSource()
    {
        return new RoadmapSnapshotSource
        {
            RoadmapId = 88,
            Title = "Hai ngày ăn ngon",
            StartDate = new DateOnly(2026, 10, 20),
            NumDays = 2,
            Days =
            [
                new RoadmapSnapshotDaySource
                {
                    DayNumber = 2
                },
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
                            Ward = "Phường Hải Châu",
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
                            Ward = "Phường Hải Châu",
                            ProvinceName = "Đà Nẵng"
                        }
                    ]
                }
            ]
        };
    }

    /// <summary>Creates a valid required-only request and optionally attaches a roadmap.</summary>
    private static CreateBlogRequestDto ValidRequest(long? roadmapId = null, string content = "Nội dung") => new()
    {
        Title = "Bài viết",
        Content = content,
        RoadmapId = roadmapId
    };

    /// <summary>Creates a unit of work with a disposable transaction mock.</summary>
    private static Mock<IUnitOfWork> UnitOfWork(out Mock<IUnitOfWorkTransaction> transaction)
    {
        transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        return unitOfWork;
    }

    /// <summary>Creates BLOG-04 with real validation and deterministic mocked boundaries.</summary>
    private static CreateBlogUseCase CreateUseCase(
        Mock<IBlogRepository>? blogRepository = null,
        Mock<IMediaRepository>? mediaRepository = null,
        Mock<IBlogContentSanitizer>? sanitizer = null,
        Mock<IUnitOfWork>? unitOfWork = null,
        Mock<ICurrentUserService>? currentUser = null)
    {
        blogRepository ??= BaseBlogRepository();
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
        return new CreateBlogUseCase(
            new CreateBlogRequestDtoValidator(),
            blogRepository.Object,
            mediaRepository.Object,
            sanitizer.Object,
            currentUser.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));
    }

    /// <summary>Provides a deterministic UTC clock for snapshot and publication timestamps.</summary>
    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        /// <summary>Returns the fixed instant supplied by the test.</summary>
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
