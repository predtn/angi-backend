using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Social.GetMyBlogs;
using ANGI.Application.UseCases.Validators.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Social.GetMyBlogs;

public sealed class GetMyBlogsUseCaseTests
{
    private static readonly DateTime PublishedDate = new(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);

    // TEST-01: Happy Path - Return personal blogs with mapped properties, excerpt, and moderation note.
    [Fact]
    public async Task ExecuteAsync_WithExistingBlogs_ShouldReturnPagedMyBlogs()
    {
        var author = new User
        {
            Id = 12,
            DisplayName = "Nguyễn An",
            AvatarMedia = new MediaFile { Url = "https://cdn.angi.vn/avatars/12.jpg" }
        };

        var blogList = new List<Blog>
        {
            new()
            {
                Id = 45,
                AuthorId = 12,
                Author = author,
                Title = "Hành trình 3 ngày ăn ở Đà Nẵng",
                Slug = "hanh-trinh-3-ngay-an-o-da-nang",
                Content = new string('A', 250), // longer than 200 chars
                CoverMedia = new MediaFile { Url = "https://cdn.angi.vn/blogs/45.jpg" },
                RoadmapSnapshot = "{\"days\":[]}",
                Status = BlogStatus.Hidden,
                LikeCount = 56,
                CommentCount = 12,
                ViewCount = 1340,
                PublishedAt = PublishedDate
            },
            new()
            {
                Id = 46,
                AuthorId = 12,
                Author = author,
                Title = "Quán ngon vỉa hè",
                Slug = "quan-ngon-via-he",
                Content = "Nội dung ngắn",
                CoverMedia = null,
                RoadmapSnapshot = null,
                Status = BlogStatus.Published,
                LikeCount = 10,
                CommentCount = 2,
                ViewCount = 100,
                PublishedAt = PublishedDate
            }
        };

        var moderationNotes = new Dictionary<long, string?>
        {
            [45] = "Nội dung quảng cáo"
        };

        var repository = new Mock<IBlogRepository>();
        repository.Setup(r => r.GetByAuthorAsync(12, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((blogList, moderationNotes, 2L));

        var useCase = CreateUseCase(repository, 12);
        var request = new GetMyBlogsRequestDto { Page = 1, PageSize = 20 };

        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        result.Should().NotBeNull();
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
        result.TotalItems.Should().Be(2);
        result.TotalPages.Should().Be(1);
        result.Items.Should().HaveCount(2);

        var first = result.Items[0];
        first.Id.Should().Be(45);
        first.Title.Should().Be("Hành trình 3 ngày ăn ở Đà Nẵng");
        first.Slug.Should().Be("hanh-trinh-3-ngay-an-o-da-nang");
        first.Excerpt.Should().HaveLength(200);
        first.CoverUrl.Should().Be("https://cdn.angi.vn/blogs/45.jpg");
        first.Author.Id.Should().Be(12);
        first.Author.DisplayName.Should().Be("Nguyễn An");
        first.Author.AvatarUrl.Should().Be("https://cdn.angi.vn/avatars/12.jpg");
        first.HasRoadmap.Should().BeTrue();
        first.Status.Should().Be("hidden");
        first.ModerationNote.Should().Be("Nội dung quảng cáo");
        first.LikeCount.Should().Be(56);
        first.CommentCount.Should().Be(12);
        first.ViewCount.Should().Be(1340);
        first.PublishedAt.Should().Be(PublishedDate);

        var second = result.Items[1];
        second.Id.Should().Be(46);
        second.Excerpt.Should().Be("Nội dung ngắn");
        second.CoverUrl.Should().BeNull();
        second.HasRoadmap.Should().BeFalse();
        second.Status.Should().Be("published");
        second.ModerationNote.Should().BeNull();
    }

    // TEST-02: Happy Path - Return empty page when user has no blogs.
    [Fact]
    public async Task ExecuteAsync_WithNoBlogs_ShouldReturnEmptyPage()
    {
        var repository = new Mock<IBlogRepository>();
        repository.Setup(r => r.GetByAuthorAsync(12, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<Blog>(), new Dictionary<long, string?>(), 0L));

        var useCase = CreateUseCase(repository, 12);
        var request = new GetMyBlogsRequestDto { Page = 1, PageSize = 20 };

        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        result.Should().NotBeNull();
        result.Items.Should().BeEmpty();
        result.TotalItems.Should().Be(0);
        result.TotalPages.Should().Be(0);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
    }

    // TEST-03: Happy Path - Filter by status (published, hidden, removed).
    [Theory]
    [InlineData("published", BlogStatus.Published)]
    [InlineData("hidden", BlogStatus.Hidden)]
    [InlineData("removed", BlogStatus.Removed)]
    public async Task ExecuteAsync_WithStatusFilter_ShouldPassParsedStatusToRepository(string statusInput, BlogStatus expectedStatus)
    {
        var repository = new Mock<IBlogRepository>();
        repository.Setup(r => r.GetByAuthorAsync(12, expectedStatus, 1, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<Blog>(), new Dictionary<long, string?>(), 0L));

        var useCase = CreateUseCase(repository, 12);
        var request = new GetMyBlogsRequestDto { Status = statusInput, Page = 1, PageSize = 10 };

        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        result.Should().NotBeNull();
        repository.Verify(r => r.GetByAuthorAsync(12, expectedStatus, 1, 10, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-04: Exception Path - Invalid Request DTO parameters throw ValidationException.
    [Theory]
    [InlineData(0, 20, null)]
    [InlineData(1, 0, null)]
    [InlineData(1, 101, null)]
    [InlineData(1, 20, "invalid_status")]
    public async Task ExecuteAsync_WithInvalidParameters_ShouldThrowValidationException(int page, int pageSize, string? status)
    {
        var repository = new Mock<IBlogRepository>();
        var useCase = CreateUseCase(repository, 12);
        var request = new GetMyBlogsRequestDto { Page = page, PageSize = pageSize, Status = status };

        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        repository.Verify(
            r => r.GetByAuthorAsync(It.IsAny<int>(), It.IsAny<BlogStatus?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // TEST-05: Exception Path - Unauthenticated request throws UnauthorizedException.
    [Fact]
    public async Task ExecuteAsync_WithoutAuthenticatedUser_ShouldThrowUnauthorizedException()
    {
        var repository = new Mock<IBlogRepository>();
        var useCase = CreateUseCase(repository, null);
        var request = new GetMyBlogsRequestDto();

        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("UNAUTHORIZED");
        repository.Verify(
            r => r.GetByAuthorAsync(It.IsAny<int>(), It.IsAny<BlogStatus?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static GetMyBlogsUseCase CreateUseCase(Mock<IBlogRepository> repository, int? userId)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.UserId).Returns(userId);

        return new GetMyBlogsUseCase(
            new GetMyBlogsRequestDtoValidator(),
            repository.Object,
            currentUser.Object);
    }
}
