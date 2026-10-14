using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Discovery;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.UseCases.Discovery.BrowseDishes;
using ANGI.Application.UseCases.Validators.Discovery;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Discovery.BrowseDishes;

public sealed class BrowseDishesUseCaseTests
{
    private const int TravelerId = 12;

    private readonly Mock<IDishRepository> _dishRepository = new();
    private readonly Mock<IRecommendationService> _recommendationService = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly InMemoryCache _cache = new();

    private BrowseDishesUseCase CreateUseCase() => new(
        new BrowseDishesRequestDtoValidator(),
        _dishRepository.Object,
        _recommendationService.Object,
        _cache,
        _currentUserService.Object);

    private void AsTraveler()
    {
        _currentUserService.SetupGet(x => x.UserId).Returns(TravelerId);
        _currentUserService.SetupGet(x => x.Role).Returns("TRAVELER");
    }

    private void Candidates(params int[] ids) =>
        _dishRepository.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids);

    private void Ranked(Guid? requestId, params int[] ids) =>
        _recommendationService.Setup(x => x.RecommendAsync(
                It.IsAny<int?>(), It.IsAny<IReadOnlyList<int>>(), 200, RecommendationContext.List, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(requestId, ids, []));

    /// <summary>Every requested id that is still a candidate comes back as a dish.</summary>
    private void CardsFor(params int[] availableIds) =>
        _dishRepository.Setup(x => x.GetCardDishesAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<int> ids, CancellationToken _) =>
                ids.Where(availableIds.Contains).Select(Dish).ToArray());

    private static Dish Dish(int id) => new()
    {
        Id = id,
        Name = $"Món {id}",
        Price = 30000,
        DishForm = DishForm.Dry,
        Restaurant = new Domain.Entities.Restaurant { Id = 7, Name = "Quán" }
    };

    // TEST-01: Build the filter, let reco rank at most 200 of the candidates for the Traveler, return page 1 with ranks.
    [Fact]
    public async Task ExecuteAsync_FirstPage_ShouldRankCandidatesAndReturnRankedPage()
    {
        AsTraveler();
        var requestId = Guid.NewGuid();
        DishCandidateFilter? filter = null;
        _dishRepository.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback<DishCandidateFilter, int, CancellationToken>((value, _, _) => filter = value)
            .ReturnsAsync([1, 2, 3]);
        Ranked(requestId, 3, 1, 2);
        CardsFor(1, 2, 3);

        var result = await CreateUseCase().ExecuteAsync(new BrowseDishesRequestDto
        {
            Q = "bún",
            TagIds = [2, 5],
            ExcludeTagIds = [9],
            DishForm = "soup",
            Lat = 16.06m,
            Lng = 108.22m,
            Limit = 2
        }, CancellationToken.None);

        filter.Should().BeEquivalentTo(new DishCandidateFilter
        {
            Query = "bún",
            TagIds = [2, 5],
            ExcludeTagIds = [9],
            DishForm = DishForm.Soup,
            Area = new NearbyArea(16.06m, 108.22m, 5m)
        });
        _recommendationService.Verify(x => x.RecommendAsync(
            TravelerId, It.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 1, 2, 3 })), 200,
            RecommendationContext.List, It.IsAny<CancellationToken>()), Times.Once);
        result.RequestId.Should().Be(requestId);
        result.Items.Select(item => (item.Id, item.Rank)).Should().Equal((3, 1), (1, 2));
        result.NextCursor.Should().NotBeNull();
    }

    // TEST-02: The next page reuses the cached list: no new candidates, no new reco call, ranks continue.
    [Fact]
    public async Task ExecuteAsync_WithCursor_ShouldPageTheCachedListWithoutCallingRecoAgain()
    {
        AsTraveler();
        var requestId = Guid.NewGuid();
        Candidates(1, 2, 3);
        Ranked(requestId, 3, 1, 2);
        CardsFor(1, 2, 3);
        var first = await CreateUseCase().ExecuteAsync(new BrowseDishesRequestDto { Limit = 2 }, CancellationToken.None);

        var second = await CreateUseCase().ExecuteAsync(
            new BrowseDishesRequestDto { Cursor = first.NextCursor, Limit = 2, Q = "ignored" },
            CancellationToken.None);

        second.RequestId.Should().Be(requestId);
        second.Items.Select(item => (item.Id, item.Rank)).Should().Equal((2, 3));
        second.NextCursor.Should().BeNull();
        _dishRepository.Verify(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _recommendationService.Verify(x => x.RecommendAsync(
            It.IsAny<int?>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<int?>(), It.IsAny<RecommendationContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Guests and non-Traveler roles are ranked without a user id, and still page with a backend list key.
    [Theory]
    [InlineData(null, null)]
    [InlineData(21, "RESTAURANT_OWNER")]
    public async Task ExecuteAsync_WithoutTraveler_ShouldRankAnonymouslyAndStillPage(int? userId, string? role)
    {
        _currentUserService.SetupGet(x => x.UserId).Returns(userId);
        _currentUserService.SetupGet(x => x.Role).Returns(role);
        Candidates(1, 2);
        Ranked(null, 1, 2);
        CardsFor(1, 2);

        var first = await CreateUseCase().ExecuteAsync(new BrowseDishesRequestDto { Limit = 1 }, CancellationToken.None);
        var second = await CreateUseCase().ExecuteAsync(new BrowseDishesRequestDto { Cursor = first.NextCursor }, CancellationToken.None);

        _recommendationService.Verify(x => x.RecommendAsync(
            null, It.IsAny<IReadOnlyList<int>>(), 200, RecommendationContext.List, It.IsAny<CancellationToken>()), Times.Once);
        first.RequestId.Should().BeNull();
        second.Items.Select(item => item.Id).Should().Equal(2);
    }

    // TEST-04: A dish hidden or sold out since the list was ranked is skipped; the others keep their rank.
    [Fact]
    public async Task ExecuteAsync_WhenDishIsNoLongerACandidate_ShouldSkipItAndKeepRanks()
    {
        Candidates(1, 2, 3);
        Ranked(null, 1, 2, 3);
        CardsFor(1, 3);

        var result = await CreateUseCase().ExecuteAsync(new BrowseDishesRequestDto(), CancellationToken.None);

        result.Items.Select(item => (item.Id, item.Rank)).Should().Equal((1, 1), (3, 3));
        result.NextCursor.Should().BeNull();
    }

    // TEST-05: Malformed, unknown, expired or another user's cursors are 400 INVALID_CURSOR.
    [Fact]
    public async Task ExecuteAsync_WithInvalidCursor_ShouldThrowInvalidCursor()
    {
        AsTraveler();
        Candidates(1, 2);
        Ranked(Guid.NewGuid(), 1, 2);
        CardsFor(1, 2);
        var travelersCursor = (await CreateUseCase().ExecuteAsync(new BrowseDishesRequestDto { Limit = 1 }, CancellationToken.None)).NextCursor;

        foreach (var cursor in new[] { "not-a-cursor", "eyJLIjoiMDAwMDAwMDAtMDAwMC0wMDAwLTAwMDAtMDAwMDAwMDAwMDAxIiwiTyI6MH0" })
        {
            var act = () => CreateUseCase().ExecuteAsync(new BrowseDishesRequestDto { Cursor = cursor }, CancellationToken.None);
            (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("INVALID_CURSOR");
        }

        _currentUserService.SetupGet(x => x.UserId).Returns(99);
        var otherUser = () => CreateUseCase().ExecuteAsync(new BrowseDishesRequestDto { Cursor = travelersCursor }, CancellationToken.None);
        (await otherUser.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("INVALID_CURSOR");
    }

    // TEST-06: Validate before anything is loaded.
    [Fact]
    public async Task ExecuteAsync_WithInvalidFilter_ShouldThrowValidationBeforeLoading()
    {
        var act = () => CreateUseCase().ExecuteAsync(new BrowseDishesRequestDto { RadiusKm = 3 }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        _dishRepository.VerifyNoOtherCalls();
        _recommendationService.VerifyNoOtherCalls();
    }

    private sealed class InMemoryCache : IRankedDishListCache
    {
        private readonly Dictionary<Guid, RankedDishList> _lists = [];

        public void Store(RankedDishList list) => _lists[list.Key] = list;

        public RankedDishList? Get(Guid key) => _lists.GetValueOrDefault(key);
    }
}
