using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Social.Create;
using ANGI.Application.UseCases.Validators.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Social.Create;

public sealed class CreateBlogUseCaseTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
    private const int TravelerUserId = 12;

    private readonly Mock<IBlogRepository> _blogRepository = new();
    private readonly Mock<IRoadmapRepository> _roadmapRepository = new();
    private readonly Mock<IMediaRepository> _mediaRepository = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly FixedTimeProvider _timeProvider = new(FixedNow);

    public CreateBlogUseCaseTests()
    {
        _currentUserService.Setup(s => s.UserId).Returns(TravelerUserId);
        _currentUserService.Setup(s => s.Role).Returns("TRAVELER");
    }

    private CreateBlogUseCase CreateUseCase() => new(
        new CreateBlogRequestDtoValidator(),
        _blogRepository.Object,
        _roadmapRepository.Object,
        _mediaRepository.Object,
        _currentUserService.Object,
        _unitOfWork.Object,
        _timeProvider);

    // TEST-01: Happy Path - Create blog successfully with full valid data (without roadmap).
    [Fact]
    public async Task ExecuteAsync_WithValidData_WithoutRoadmap_ShouldCreateBlogSuccessfully()
    {
        // Arrange
        var request = new CreateBlogRequestDto
        {
            Title = "Hành trình ẩm thực Đà Nẵng",
            Content = "<p>Ngày đầu tiên khám phá ẩm thực...</p>",
            CoverMediaId = 101,
            ImageMediaIds = [102, 103]
        };

        _blogRepository.Setup(r => r.SlugExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _mediaRepository.Setup(r => r.GetOwnedByIdsAsync(TravelerUserId, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new MediaFile { Id = 101, UploaderId = TravelerUserId, Url = "https://cdn.angi.vn/cover.jpg" },
                new MediaFile { Id = 102, UploaderId = TravelerUserId, Url = "https://cdn.angi.vn/img1.jpg" },
                new MediaFile { Id = 103, UploaderId = TravelerUserId, Url = "https://cdn.angi.vn/img2.jpg" }
            ]);

        Blog? savedBlog = null;
        _blogRepository.Setup(r => r.Add(It.IsAny<Blog>()))
            .Callback<Blog>(b =>
            {
                savedBlog = b;
                b.Id = 45;
                b.Author = new User { Id = TravelerUserId, DisplayName = "Nguyễn An" };
                b.CoverMedia = new MediaFile { Id = 101, Url = "https://cdn.angi.vn/cover.jpg" };
            });

        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _blogRepository.Setup(r => r.GetByIdWithDetailsAsync(45, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => savedBlog);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(45);
        result.Title.Should().Be("Hành trình ẩm thực Đà Nẵng");
        result.Slug.Should().Be("hanh-trinh-am-thuc-da-nang");
        result.Content.Should().Be("<p>Ngày đầu tiên khám phá ẩm thực...</p>");
        result.CoverUrl.Should().Be("https://cdn.angi.vn/cover.jpg");
        result.Author.Id.Should().Be(TravelerUserId);
        result.Author.DisplayName.Should().Be("Nguyễn An");
        result.HasRoadmap.Should().BeFalse();
        result.RoadmapId.Should().BeNull();
        result.RoadmapSnapshot.Should().BeNull();
        result.IsMine.Should().BeTrue();
        result.LikedByMe.Should().BeFalse();
        result.PublishedAt.Should().Be(FixedNow.UtcDateTime);

        savedBlog.Should().NotBeNull();
        savedBlog!.Status.Should().Be(BlogStatus.Published);
        savedBlog.Images.Should().HaveCount(2);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Happy Path - Create blog with roadmap snapshot (Share My Roadmap).
    [Fact]
    public async Task ExecuteAsync_WithValidRoadmap_ShouldCreateBlogWithSnapshot()
    {
        // Arrange
        const long roadmapId = 88;
        var request = new CreateBlogRequestDto
        {
            Title = "3 ngày ăn sập Đà Nẵng",
            Content = "<p>Lộ trình ăn uống chi tiết...</p>",
            RoadmapId = roadmapId
        };

        var roadmap = new Domain.Entities.Roadmap
        {
            Id = roadmapId,
            UserId = TravelerUserId,
            Title = "3 ngày ăn sập Đà Nẵng",
            StartDate = new DateOnly(2026, 10, 10),
            NumDays = 1,
            Days =
            [
                new RoadmapDay
                {
                    Id = 1,
                    DayNumber = 1,
                    Items =
                    [
                        new RoadmapItem
                        {
                            Id = 10,
                            SortOrder = 0,
                            MealSlot = MealSlot.Breakfast,
                            DishId = 105,
                            Dish = new Dish
                            {
                                Id = 105,
                                Name = "Bún bò Huế",
                                Price = 45000,
                                RestaurantId = 7,
                                Restaurant = new Domain.Entities.Restaurant
                                {
                                    Id = 7,
                                    Name = "Bún Bò Bà Diệu",
                                    AddressLine = "12 Lê Duẩn",
                                    District = "Quận Hải Châu"
                                }
                            }
                        }
                    ]
                }
            ]
        };

        _roadmapRepository.Setup(r => r.GetByIdForSnapshotAsync(roadmapId, TravelerUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(roadmap);

        _blogRepository.Setup(r => r.SlugExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Blog? savedBlog = null;
        _blogRepository.Setup(r => r.Add(It.IsAny<Blog>()))
            .Callback<Blog>(b =>
            {
                savedBlog = b;
                b.Id = 50;
                b.Author = new User { Id = TravelerUserId, DisplayName = "Nguyễn An" };
            });

        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _blogRepository.Setup(r => r.GetByIdWithDetailsAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => savedBlog);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.HasRoadmap.Should().BeTrue();
        result.RoadmapId.Should().Be(roadmapId);
        result.RoadmapSnapshot.Should().NotBeNull();
        result.RoadmapSnapshot!.RoadmapId.Should().Be(roadmapId);
        result.RoadmapSnapshot.Title.Should().Be("3 ngày ăn sập Đà Nẵng");
        result.RoadmapSnapshot.NumDays.Should().Be(1);
        result.RoadmapSnapshot.TotalCost.Should().Be(45000);
        result.RoadmapSnapshot.Days.Should().HaveCount(1);
        result.RoadmapSnapshot.Days[0].Items.Should().HaveCount(1);
        result.RoadmapSnapshot.Days[0].Items[0].DishName.Should().Be("Bún bò Huế");
        result.RoadmapSnapshot.Days[0].Items[0].MealSlot.Should().Be("breakfast");
        result.RoadmapSnapshot.Days[0].Items[0].RestaurantName.Should().Be("Bún Bò Bà Diệu");

        savedBlog.Should().NotBeNull();
        savedBlog!.RoadmapSnapshot.Should().NotBeNullOrWhiteSpace();
        savedBlog.RoadmapSnapshot.Should().Contain("\"dishId\":105");
    }

    // TEST-03: Exception Path 1 - Validation Error (Empty Title).
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ExecuteAsync_WithMissingTitle_ShouldThrowValidationException(string? invalidTitle)
    {
        // Arrange
        var request = new CreateBlogRequestDto
        {
            Title = invalidTitle!,
            Content = "Nội dung hợp lệ"
        };
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().Contain(e => e.PropertyName == nameof(CreateBlogRequestDto.Title));
    }

    // TEST-04: Exception Path 1 - Validation Error (Empty Content).
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ExecuteAsync_WithMissingContent_ShouldThrowValidationException(string? invalidContent)
    {
        // Arrange
        var request = new CreateBlogRequestDto
        {
            Title = "Tiêu đề hợp lệ",
            Content = invalidContent!
        };
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().Contain(e => e.PropertyName == nameof(CreateBlogRequestDto.Content));
    }

    // TEST-05: Exception Path 2 - Roadmap Not Found.
    [Fact]
    public async Task ExecuteAsync_WithNonExistentRoadmap_ShouldThrowNotFoundException()
    {
        // Arrange
        const long nonExistentRoadmapId = 999;
        var request = new CreateBlogRequestDto
        {
            Title = "Tiêu đề hợp lệ",
            Content = "Nội dung hợp lệ",
            RoadmapId = nonExistentRoadmapId
        };

        _roadmapRepository.Setup(r => r.GetByIdForSnapshotAsync(nonExistentRoadmapId, TravelerUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Domain.Entities.Roadmap?)null);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("ROADMAP_NOT_FOUND");
        ex.Which.Message.Should().Be("Không tìm thấy lộ trình.");
    }

    // TEST-06: Exception Path - Unauthorized (User not logged in).
    [Fact]
    public async Task ExecuteAsync_WhenUserNotAuthenticated_ShouldThrowUnauthorizedException()
    {
        // Arrange
        _currentUserService.Setup(s => s.UserId).Returns((int?)null);

        var request = new CreateBlogRequestDto
        {
            Title = "Tiêu đề",
            Content = "Nội dung"
        };
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<UnauthorizedException>();
        ex.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    // TEST-07: Exception Path - Forbidden (User is not a Traveler).
    [Fact]
    public async Task ExecuteAsync_WhenUserIsNotTraveler_ShouldThrowForbiddenException()
    {
        // Arrange
        _currentUserService.Setup(s => s.Role).Returns("RESTAURANT_OWNER");

        var request = new CreateBlogRequestDto
        {
            Title = "Tiêu đề",
            Content = "Nội dung"
        };
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<ForbiddenException>();
        ex.Which.ErrorCode.Should().Be("FORBIDDEN");
    }

    // TEST-08: Exception Path - Media not owned by user.
    [Fact]
    public async Task ExecuteAsync_WithUnownedMedia_ShouldThrowValidationException()
    {
        // Arrange
        var request = new CreateBlogRequestDto
        {
            Title = "Tiêu đề",
            Content = "Nội dung",
            CoverMediaId = 999
        };

        _mediaRepository.Setup(r => r.GetOwnedByIdsAsync(TravelerUserId, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().Contain(e => e.PropertyName == nameof(CreateBlogRequestDto.CoverMediaId));
    }

    // TEST-09: Slug collision appends numerical suffix.
    [Fact]
    public async Task ExecuteAsync_WithSlugCollision_ShouldAppendSuffix()
    {
        // Arrange
        var request = new CreateBlogRequestDto
        {
            Title = "Món ăn ngon",
            Content = "Nội dung"
        };

        _blogRepository.SetupSequence(r => r.SlugExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)   // "mon-an-ngon" exists
            .ReturnsAsync(false);  // "mon-an-ngon-2" available

        Blog? savedBlog = null;
        _blogRepository.Setup(r => r.Add(It.IsAny<Blog>()))
            .Callback<Blog>(b => savedBlog = b);

        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _blogRepository.Setup(r => r.GetByIdWithDetailsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => savedBlog);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.Slug.Should().Be("mon-an-ngon-2");
        savedBlog!.Slug.Should().Be("mon-an-ngon-2");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
