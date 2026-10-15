using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Discovery.GetHome;
using ANGI.Application.UseCases.Validators.Discovery;
using ANGI.Domain.Entities;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Discovery.GetHome;

public sealed class GetHomeUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 3, 0, 0, TimeSpan.Zero);

    private readonly Mock<IDishRepository> _dishRepository = new();
    private readonly Mock<IRestaurantRepository> _restaurantRepository = new();
    private readonly Mock<IBlogRepository> _blogRepository = new();
    private readonly Mock<IRecommendationService> _recommendationService = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();

    public GetHomeUseCaseTests()
    {
        _dishRepository.Setup(x => x.GetTrendingIdsAsync(It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _dishRepository.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _dishRepository.Setup(x => x.GetCardDishesAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<int> ids, CancellationToken _) => ids.Select(Dish).ToArray());
        _restaurantRepository.Setup(x => x.BrowseAsync(It.IsAny<BrowseRestaurantsRequestDto>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Paged<RestaurantCardDto> { Items = [] });
        _blogRepository.Setup(x => x.GetFeaturedAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _recommendationService.Setup(x => x.RecommendAsync(
                It.IsAny<int?>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<int?>(), It.IsAny<RecommendationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(null, [], []));
    }

    private GetHomeUseCase CreateUseCase() => new(
        new GetHomeRequestDtoValidator(),
        _dishRepository.Object,
        _restaurantRepository.Object,
        _blogRepository.Object,
        _recommendationService.Object,
        _currentUserService.Object,
        new FixedTimeProvider(Now));

    private static Dish Dish(int id) => new()
    {
        Id = id,
        Name = $"Món {id}",
        Restaurant = new Domain.Entities.Restaurant { Id = 7, Name = "Quán" }
    };

    // TEST-01: Trending keeps the repository order without ranks, over the 7-day window, filtered by province.
    [Fact]
    public async Task ExecuteAsync_ShouldReturnTrendingInOrderWithoutRank()
    {
        _dishRepository.Setup(x => x.GetTrendingIdsAsync("Đà Nẵng", Now.UtcDateTime.AddDays(-7), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync([3, 1]);

        var result = await CreateUseCase().ExecuteAsync(new GetHomeRequestDto { Province = "Đà Nẵng", Limit = 5 }, CancellationToken.None);

        result.TrendingDishes.Select(dish => (dish.Id, dish.Rank)).Should().Equal((3, (int?)null), (1, (int?)null));
    }

    // TEST-02: Top restaurants reuse REST-01 sorted by rating; blogs take the 30-day window and become BlogCards.
    [Fact]
    public async Task ExecuteAsync_ShouldBuildTopRestaurantsAndFeaturedBlogs()
    {
        var restaurant = new RestaurantCardDto { Id = 7, Name = "Quán" };
        _restaurantRepository.Setup(x => x.BrowseAsync(
                It.Is<BrowseRestaurantsRequestDto>(r => r.Province == "Huế" && r.Sort == "rating" && r.Page == 1 && r.PageSize == 4),
                Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Paged<RestaurantCardDto> { Items = [restaurant] });
        _blogRepository.Setup(x => x.GetFeaturedAsync(Now.UtcDateTime.AddDays(-30), 4, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new Blog
                {
                    Id = 45,
                    Title = "Ăn sập Huế",
                    Slug = "an-sap-hue",
                    Content = new string('a', 250),
                    RoadmapSnapshot = "{}",
                    LikeCount = 56,
                    CommentCount = 12,
                    Author = new User { Id = 12, DisplayName = "An", AvatarMedia = new MediaFile { Url = "https://cdn.test/a.jpg" } }
                }
            ]);

        var result = await CreateUseCase().ExecuteAsync(new GetHomeRequestDto { Province = "Huế", Limit = 4 }, CancellationToken.None);

        result.TopRestaurants.Should().Equal(restaurant);
        result.FeaturedBlogs.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Id = 45L,
            Excerpt = new string('a', 200),
            HasRoadmap = true,
            LikeCount = 56,
            CommentCount = 12,
            Author = new { Id = 12, DisplayName = "An", AvatarUrl = "https://cdn.test/a.jpg" }
        });
    }

    // TEST-03: A Traveler's recommended block goes through reco with context = home and keeps reco's order and request id.
    [Fact]
    public async Task ExecuteAsync_ForTraveler_ShouldRankRecommendedThroughReco()
    {
        _currentUserService.SetupGet(x => x.UserId).Returns(12);
        _currentUserService.SetupGet(x => x.Role).Returns("TRAVELER");
        var requestId = Guid.NewGuid();
        _dishRepository.Setup(x => x.GetCandidateIdsAsync(
                It.Is<DishCandidateFilter>(f => f.Province == "Đà Nẵng"), 2000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([1, 2, 3]);
        _recommendationService.Setup(x => x.RecommendAsync(
                12, It.IsAny<IReadOnlyList<int>>(), 10, RecommendationContext.Home, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(requestId, [2, 3, 1], []));

        var result = await CreateUseCase().ExecuteAsync(new GetHomeRequestDto { Province = "Đà Nẵng" }, CancellationToken.None);

        result.Recommended.RequestId.Should().Be(requestId);
        result.Recommended.Items.Select(dish => (dish.Id, dish.Rank)).Should().Equal((2, (int?)1), (3, (int?)2), (1, (int?)3));
    }

    // TEST-04: Guests and other roles are ranked without a user id.
    [Theory]
    [InlineData(null, null)]
    [InlineData(21, "RESTAURANT_OWNER")]
    public async Task ExecuteAsync_WithoutTraveler_ShouldNotPersonalize(int? userId, string? role)
    {
        _currentUserService.SetupGet(x => x.UserId).Returns(userId);
        _currentUserService.SetupGet(x => x.Role).Returns(role);

        await CreateUseCase().ExecuteAsync(new GetHomeRequestDto(), CancellationToken.None);

        _recommendationService.Verify(x => x.RecommendAsync(
            null, It.IsAny<IReadOnlyList<int>>(), 10, RecommendationContext.Home, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-05: Reject a limit outside 1..50 before loading anything.
    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task ExecuteAsync_WithInvalidLimit_ShouldThrowValidation(int limit)
    {
        var act = () => CreateUseCase().ExecuteAsync(new GetHomeRequestDto { Limit = limit }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        _dishRepository.Verify(x => x.GetTrendingIdsAsync(It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
