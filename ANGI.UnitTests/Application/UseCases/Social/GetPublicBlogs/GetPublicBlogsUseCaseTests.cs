using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Social.GetPublicBlogs;
using ANGI.Application.UseCases.Validators.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Social.GetPublicBlogs;

/// <summary>Unit tests for BLOG-01 public blog listing use case.</summary>
public sealed class GetPublicBlogsUseCaseTests
{
    private readonly IValidator<GetPublicBlogsRequestDto> _validator = new GetPublicBlogsRequestDtoValidator();
    private readonly Mock<IBlogRepository> _blogRepository = new();

    private GetPublicBlogsUseCase CreateUseCase() =>
        new(_validator, _blogRepository.Object);

    // Happy Path 1: Có dữ liệu blog công khai, trả về đúng cấu trúc phân trang và mapping BlogCardDto
    [Fact]
    public async Task ExecuteAsync_WhenPublicBlogsExist_ShouldReturnPaginatedBlogCards()
    {
        // Arrange
        var request = new GetPublicBlogsRequestDto
        {
            Q = "Đà Nẵng",
            AuthorId = 12,
            HasRoadmap = true,
            Sort = "popular",
            Page = 1,
            PageSize = 20
        };

        var longContent = new string('A', 210);
        var publishedAt1 = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
        var publishedAt2 = new DateTime(2026, 10, 12, 8, 30, 0, DateTimeKind.Utc);

        var blogs = new List<Blog>
        {
            new()
            {
                Id = 45,
                AuthorId = 12,
                Title = "Hành trình 3 ngày ăn ở Đà Nẵng",
                Slug = "hanh-trinh-3-ngay-an-o-da-nang",
                Content = "  Ngày đầu tiên mình bắt đầu bằng tô bún bò...  ",
                RoadmapSnapshot = "{\"roadmapId\":88}",
                Status = BlogStatus.Published,
                LikeCount = 56,
                CommentCount = 12,
                ViewCount = 1340,
                PublishedAt = publishedAt1,
                CoverMedia = new MediaFile { Id = 100, Url = "https://cdn.angi.vn/blogs/45.jpg" },
                Author = new User
                {
                    Id = 12,
                    DisplayName = "Nguyễn An",
                    AvatarMedia = new MediaFile { Id = 101, Url = "https://cdn.angi.vn/avatars/12.jpg" }
                }
            },
            new()
            {
                Id = 46,
                AuthorId = 12,
                Title = "Khám phá ẩm thực phố cổ Hội An",
                Slug = "kham-pha-am-thuc-pho-co-hoi-an",
                Content = longContent,
                RoadmapSnapshot = null,
                Status = BlogStatus.Published,
                LikeCount = 20,
                CommentCount = 5,
                ViewCount = 410,
                PublishedAt = publishedAt2,
                CoverMedia = null,
                Author = new User
                {
                    Id = 12,
                    DisplayName = "Nguyễn An",
                    AvatarMedia = null
                }
            }
        };

        _blogRepository
            .Setup(r => r.GetPublicBlogsAsync(
                request.Q,
                request.AuthorId,
                request.HasRoadmap,
                request.Sort,
                request.Page,
                request.PageSize,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((blogs, 57L));

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
        result.TotalItems.Should().Be(57L);
        result.TotalPages.Should().Be(3); // ceil(57 / 20) = 3
        result.Items.Should().HaveCount(2);

        var first = result.Items[0];
        first.Id.Should().Be(45);
        first.Title.Should().Be("Hành trình 3 ngày ăn ở Đà Nẵng");
        first.Slug.Should().Be("hanh-trinh-3-ngay-an-o-da-nang");
        first.Excerpt.Should().Be("Ngày đầu tiên mình bắt đầu bằng tô bún bò...");
        first.CoverUrl.Should().Be("https://cdn.angi.vn/blogs/45.jpg");
        first.Author.Id.Should().Be(12);
        first.Author.DisplayName.Should().Be("Nguyễn An");
        first.Author.AvatarUrl.Should().Be("https://cdn.angi.vn/avatars/12.jpg");
        first.HasRoadmap.Should().BeTrue();
        first.LikeCount.Should().Be(56);
        first.CommentCount.Should().Be(12);
        first.ViewCount.Should().Be(1340);
        first.PublishedAt.Should().Be(publishedAt1);

        var second = result.Items[1];
        second.Id.Should().Be(46);
        second.Excerpt.Should().HaveLength(GetPublicBlogsUseCase.ExcerptLength);
        second.CoverUrl.Should().BeNull();
        second.Author.AvatarUrl.Should().BeNull();
        second.HasRoadmap.Should().BeFalse();
    }

    // Happy Path 2: Danh sách rỗng, trả về đúng cấu trúc phân trang (Items rỗng, TotalItems = 0, TotalPages = 0)
    [Fact]
    public async Task ExecuteAsync_WhenNoPublicBlogsExist_ShouldReturnEmptyPage()
    {
        // Arrange
        var request = new GetPublicBlogsRequestDto
        {
            Page = 1,
            PageSize = 20
        };

        _blogRepository
            .Setup(r => r.GetPublicBlogsAsync(
                null,
                null,
                null,
                null,
                1,
                20,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<Blog>(), 0L));

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Items.Should().BeEmpty();
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
        result.TotalItems.Should().Be(0L);
        result.TotalPages.Should().Be(0);
    }

    // Exception Path 1: Tham số phân trang Page <= 0
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ExecuteAsync_WhenPageIsNotPositive_ShouldThrowValidationException(int invalidPage)
    {
        // Arrange
        var request = new GetPublicBlogsRequestDto
        {
            Page = invalidPage,
            PageSize = 20
        };

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*page*");
        _blogRepository.VerifyNoOtherCalls();
    }

    // Exception Path 2: Tham số phân trang PageSize ngoài khoảng 1..100
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    public async Task ExecuteAsync_WhenPageSizeIsOutOfRange_ShouldThrowValidationException(int invalidPageSize)
    {
        // Arrange
        var request = new GetPublicBlogsRequestDto
        {
            Page = 1,
            PageSize = invalidPageSize
        };

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*pageSize*");
        _blogRepository.VerifyNoOtherCalls();
    }

    // Exception Path 3: Tham số Sort hoặc AuthorId không hợp lệ
    [Fact]
    public async Task ExecuteAsync_WhenSortOrAuthorIdIsInvalid_ShouldThrowValidationException()
    {
        // Arrange
        var request = new GetPublicBlogsRequestDto
        {
            AuthorId = 0,
            Sort = "oldest",
            Page = 1,
            PageSize = 20
        };

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.Errors.Select(e => e.PropertyName)
            .Should().Contain([nameof(GetPublicBlogsRequestDto.AuthorId), nameof(GetPublicBlogsRequestDto.Sort)]);
        _blogRepository.VerifyNoOtherCalls();
    }
}
