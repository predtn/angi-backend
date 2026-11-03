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

/// <summary>Verifies BrowseDishesUseCase ranks candidates once through reco, pages the cached list by cursor and rejects invalid cursors.</summary>
public sealed class BrowseDishesUseCaseTests
{
    private const int TravelerId = 12;

    private readonly Mock<IDishRepository> _dishRepository = new();
    private readonly Mock<IRecommendationService> _recommendationService = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly InMemoryCache _cache = new();

    /// <summary>Creates the use case with the real validator, the in-memory list cache and the shared mocks.</summary>
    private BrowseDishesUseCase CreateUseCase() => new(
        new BrowseDishesRequestDtoValidator(),
        _dishRepository.Object,
        _recommendationService.Object,
        _cache,
        _currentUserService.Object);

    /// <summary>Signs the current user in as the default traveler.</summary>
    private void AsTraveler()
    {
        _currentUserService.SetupGet(x => x.UserId).Returns(TravelerId);
        _currentUserService.SetupGet(x => x.Role).Returns("TRAVELER");
    }

    /// <summary>Makes the repository return the given candidate dish ids for the 2000-candidate query.</summary>
    private void Candidates(params int[] ids) =>
        _dishRepository.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids);

    /// <summary>Makes reco return the given ranking and request id for the list context.</summary>
    private void Ranked(Guid? requestId, params int[] ids) =>
        _recommendationService.Setup(x => x.RecommendAsync(
                It.IsAny<int?>(), It.IsAny<IReadOnlyList<int>>(), 200, RecommendationContext.List, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(requestId, ids, []));

    /// <summary>Every requested id that is still a candidate comes back as a dish.</summary>
    private void CardsFor(params int[] availableIds) =>
        _dishRepository.Setup(x => x.GetCardDishesAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<int> ids, CancellationToken _) =>
                ids.Where(availableIds.Contains).Select(Dish).ToArray());

    /// <summary>Creates a minimal available dish with its restaurant for page mapping.</summary>
    private static Dish Dish(int id) => new()
    {
        Id = id,
        Name = $"Món {id}",
        Price = 30000,
        DishForm = DishForm.Dry,
        Restaurant = new Domain.Entities.Restaurant { Id = 7, Name = "Quán" }
    };

    // TEST-01: Build the filter, let reco rank at most 200 of the candidates for the Traveler, return page 1 with ranks.
    /// <summary>Ensures the first page is built from one reco ranking of the filtered candidates.</summary>
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
    /// <summary>Ensures later pages are stable and cost no extra candidate query or reco call.</summary>
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
    /// <summary>Ensures guests and other roles get unpersonalized ranking but the same paging.</summary>
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
    /// <summary>Ensures dishes that became unavailable after ranking are not shown and ranks stay consistent.</summary>
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
    /// <summary>Ensures a cursor cannot be forged, reused after expiry or borrowed from another user.</summary>
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
    /// <summary>Ensures an invalid filter costs no database or reco work.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidFilter_ShouldThrowValidationBeforeLoading()
    {
        var act = () => CreateUseCase().ExecuteAsync(new BrowseDishesRequestDto { RadiusKm = 3 }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        _dishRepository.VerifyNoOtherCalls();
        _recommendationService.VerifyNoOtherCalls();
    }

    /// <summary>Dictionary-backed ranked list cache so paging can be tested without Redis.</summary>
    private sealed class InMemoryCache : IRankedDishListCache
    {
        private readonly Dictionary<Guid, RankedDishList> _lists = [];

        /// <summary>Stores the list under its key, replacing any earlier list.</summary>
        public void Store(RankedDishList list) => _lists[list.Key] = list;

        /// <summary>Returns the list stored under the key, or null when there is none.</summary>
        public RankedDishList? Get(Guid key) => _lists.GetValueOrDefault(key);
    }
}
