using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Social.GetBlogDetail;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Social.GetBlogDetail;

/// <summary>Unit tests for BLOG-02 (<see cref="GetBlogDetailUseCase"/>).</summary>
public sealed class GetBlogDetailUseCaseTests
{
    private readonly Mock<IBlogRepository> _blogRepository = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();

    private GetBlogDetailUseCase CreateUseCase() =>
        new(_blogRepository.Object, _currentUserService.Object);

    // Happy Path 1: Lấy thông tin chi tiết blog thành công cho Guest (kèm dữ liệu map chính xác)
    // TEST-01: Verify ExecuteAsync when blog exists for guest; should return mapped blog detail dto.
    /// <summary>Verify ExecuteAsync when blog exists for guest; should return mapped blog detail dto.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenBlogExistsForGuest_ShouldReturnMappedBlogDetailDto()
    {
        // Arrange
        const long blogId = 45;
        _currentUserService.Setup(s => s.UserId).Returns((int?)null);

        var publishedAt = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
        var updatedAt = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

        var snapshot = new RoadmapSnapshotDto
        {
            RoadmapId = 88,
            Title = "3 ngày ăn sập Đà Nẵng",
            StartDate = new DateOnly(2026, 10, 10),
            NumDays = 3,
            TotalCost = 640000m,
            CapturedAt = publishedAt,
            Days =
            [
                new RoadmapSnapshotDayDto
                {
                    DayNumber = 1,
                    Items =
                    [
                        new RoadmapSnapshotItemDto
                        {
                            MealSlot = "breakfast",
                            DishId = 105,
                            DishName = "Bún bò Huế",
                            Price = 45000m,
                            RestaurantId = 7,
                            RestaurantName = "Bún Bò Bà Diệu",
                            Address = "12 Lê Duẩn, Quận Hải Châu"
                        }
                    ]
                }
            ]
        };

        var blog = new Blog
        {
            Id = blogId,
            AuthorId = 12,
            RoadmapId = 88,
            RoadmapSnapshot = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Title = "Hành trình 3 ngày ăn ở Đà Nẵng",
            Slug = "hanh-trinh-3-ngay-an-o-da-nang",
            Content = "<p>Ngày đầu tiên mình bắt đầu bằng tô bún bò...</p>",
            CoverMediaId = 900,
            CoverMedia = new MediaFile { Id = 900, Url = "https://cdn.angi.vn/blogs/45.jpg" },
            Status = BlogStatus.Published,
            PublishedAt = publishedAt,
            LikeCount = 56,
            CommentCount = 12,
            ViewCount = 1340,
            CreatedAt = publishedAt,
            UpdatedAt = updatedAt,
            Author = new User
            {
                Id = 12,
                DisplayName = "Nguyễn An",
                AvatarMedia = new MediaFile { Id = 800, Url = "https://cdn.angi.vn/avatars/12.jpg" }
            },
            Images =
            [
                new BlogImage
                {
                    BlogId = blogId,
                    MediaId = 902,
                    SortOrder = 1,
                    Media = new MediaFile { Id = 902, Url = "https://cdn.angi.vn/blogs/45-2.jpg" }
                },
                new BlogImage
                {
                    BlogId = blogId,
                    MediaId = 901,
                    SortOrder = 0,
                    Media = new MediaFile { Id = 901, Url = "https://cdn.angi.vn/blogs/45-1.jpg" }
                }
            ]
        };

        _blogRepository
            .Setup(r => r.GetPublicDetailAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(blogId, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(blogId);
        result.Title.Should().Be("Hành trình 3 ngày ăn ở Đà Nẵng");
        result.Slug.Should().Be("hanh-trinh-3-ngay-an-o-da-nang");
        result.Excerpt.Should().Be("<p>Ngày đầu tiên mình bắt đầu bằng tô bún bò...</p>");
        result.CoverUrl.Should().Be("https://cdn.angi.vn/blogs/45.jpg");
        result.Author.Id.Should().Be(12);
        result.Author.DisplayName.Should().Be("Nguyễn An");
        result.Author.AvatarUrl.Should().Be("https://cdn.angi.vn/avatars/12.jpg");
        result.HasRoadmap.Should().BeTrue();
        result.LikeCount.Should().Be(56);
        result.CommentCount.Should().Be(12);
        result.ViewCount.Should().Be(1340);
        result.PublishedAt.Should().Be(publishedAt);
        result.Content.Should().Be("<p>Ngày đầu tiên mình bắt đầu bằng tô bún bò...</p>");
        result.Images.Should().Equal(
            "https://cdn.angi.vn/blogs/45-1.jpg",
            "https://cdn.angi.vn/blogs/45-2.jpg");
        result.RoadmapId.Should().Be(88);
        result.RoadmapSnapshot.Should().NotBeNull();
        result.RoadmapSnapshot!.RoadmapId.Should().Be(88);
        result.RoadmapSnapshot.Title.Should().Be("3 ngày ăn sập Đà Nẵng");
        result.RoadmapSnapshot.Days.Should().ContainSingle();
        result.LikedByMe.Should().BeFalse();
        result.IsMine.Should().BeFalse();
        result.UpdatedAt.Should().Be(updatedAt);

        _blogRepository.Verify(r => r.IsLikedByUserAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Happy Path 2: Người dùng đã đăng nhập là tác giả và đã like bài viết, nội dung dài > 200 ký tự
    // TEST-02: Verify ExecuteAsync when authenticated author liked blog; should set liked by me and is mine true.
    /// <summary>Verify ExecuteAsync when authenticated author liked blog; should set liked by me and is mine true.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenAuthenticatedAuthorLikedBlog_ShouldSetLikedByMeAndIsMineTrue()
    {
        // Arrange
        const long blogId = 45;
        const int userId = 12;
        _currentUserService.Setup(s => s.UserId).Returns(userId);

        var longContent = new string('a', 250);
        var blog = new Blog
        {
            Id = blogId,
            AuthorId = userId,
            Title = "Bài viết dài",
            Slug = "bai-viet-dai",
            Content = longContent,
            Status = BlogStatus.Published,
            PublishedAt = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc),
            Author = new User
            {
                Id = userId,
                DisplayName = "Nguyễn An"
            }
        };

        _blogRepository
            .Setup(r => r.GetPublicDetailAsync(blogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blog);
        _blogRepository
            .Setup(r => r.IsLikedByUserAsync(blogId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(blogId, CancellationToken.None);

        // Assert
        result.IsMine.Should().BeTrue();
        result.LikedByMe.Should().BeTrue();
        result.HasRoadmap.Should().BeFalse();
        result.RoadmapSnapshot.Should().BeNull();
        result.Excerpt.Should().HaveLength(200);
    }

    // Exception Path: Bắn lỗi 404 BLOG_NOT_FOUND khi truyền blogId không tồn tại
    // TEST-03: Verify ExecuteAsync when blog does not exist; should throw not found exception.
    /// <summary>Verify ExecuteAsync when blog does not exist; should throw not found exception.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenBlogDoesNotExist_ShouldThrowNotFoundException()
    {
        // Arrange
        const long nonExistentBlogId = 9999;
        _blogRepository
            .Setup(r => r.GetPublicDetailAsync(nonExistentBlogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Blog?)null);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(nonExistentBlogId, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("BLOG_NOT_FOUND");
    }
}
