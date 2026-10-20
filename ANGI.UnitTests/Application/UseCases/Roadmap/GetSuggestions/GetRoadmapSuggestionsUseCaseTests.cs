using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Discovery;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Roadmap.GetSuggestions;
using ANGI.Application.UseCases.Validators.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Roadmap.GetSuggestions;

public sealed class GetRoadmapSuggestionsUseCaseTests
{
    private const int TravelerId = 12;
    private const long RoadmapId = 88;

    private readonly Mock<IRoadmapRepository> _roadmapRepository = new();
    private readonly Mock<IDishRepository> _dishRepository = new();
    private readonly Mock<IRecommendationService> _recommendationService = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();
    private readonly InMemoryCache _cache = new();

    // TEST-01: Build every RM-06 filter, exclude existing roadmap dishes, cap candidates, rank, map, and page.
    /// <summary>Verifies the principal GPS-filtered and personalized RM-06 flow.</summary>
    [Fact]
    public async Task ExecuteAsync_WithGpsFilters_ShouldRankAndReturnFirstPage()
    {
        var requestId = Guid.NewGuid();
        DishCandidateFilter? captured = null;
        SetupOwned(new RoadmapSuggestionContext(RoadmapOriginMode.Pinned, 11, 107, [9, 10]));
        _dishRepository.Setup(item => item.GetCandidateIdsAsync(
                It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback<DishCandidateFilter, int, CancellationToken>((filter, _, _) => captured = filter)
            .ReturnsAsync([1, 2, 3]);
        _recommendationService.Setup(item => item.RecommendAsync(
                TravelerId,
                It.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 1, 2, 3 })),
                200,
                RecommendationContext.RoadmapManual,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(requestId, [3, 1, 2], []));
        CardsFor(1, 2, 3);

        var result = await CreateUseCase().ExecuteAsync(RoadmapId, new GetRoadmapSuggestionsRequestDto
        {
            Province = "Đà Nẵng",
            DayId = 301,
            MealSlot = "lunch",
            Q = "bún",
            TagIds = [2],
            ExcludeTagIds = [5],
            MaxPrice = 50_000,
            Lat = 16.06m,
            Lng = 108.22m,
            RadiusKm = 7,
            Limit = 2
        }, CancellationToken.None);

        captured.Should().BeEquivalentTo(new DishCandidateFilter
        {
            Province = "Đà Nẵng",
            Query = "bún",
            TagIds = [2],
            ExcludeTagIds = [5],
            MealTimeCode = "lunch",
            MaxPrice = 50_000,
            ExcludeDishIds = new[] { 9, 10 },
            Area = new NearbyArea(16.06m, 108.22m, 7)
        });
        result.RequestId.Should().Be(requestId);
        result.Items.Select(item => (item.Id, item.Rank)).Should().Equal((3, 1), (1, 2));
        result.NextCursor.Should().NotBeNull();
    }

    // TEST-02: Use the pinned roadmap origin and 10 km default when request GPS is absent.
    /// <summary>Verifies RM-06 origin fallback and nullable requestId during reco fallback.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutGps_ShouldUsePinnedOriginAndFallbackRequestId()
    {
        DishCandidateFilter? captured = null;
        SetupOwned(new RoadmapSuggestionContext(RoadmapOriginMode.Pinned, 16.1m, 108.3m, []));
        _dishRepository.Setup(item => item.GetCandidateIdsAsync(
                It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback<DishCandidateFilter, int, CancellationToken>((filter, _, _) => captured = filter)
            .ReturnsAsync([1]);
        _recommendationService.Setup(item => item.RecommendAsync(
                TravelerId, It.IsAny<IReadOnlyList<int>>(), 200,
                RecommendationContext.RoadmapManual, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(null, [1], []));
        CardsFor(1);

        var result = await CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "Đà Nẵng" }, CancellationToken.None);

        captured!.Area.Should().Be(new NearbyArea(16.1m, 108.3m, 10));
        result.RequestId.Should().BeNull();
    }

    // TEST-03: Filter only by province when neither GPS nor a pinned point exists.
    /// <summary>Verifies none and gps roadmap modes never reuse absent persisted coordinates.</summary>
    [Theory]
    [InlineData(RoadmapOriginMode.None)]
    [InlineData(RoadmapOriginMode.Gps)]
    public async Task ExecuteAsync_WithoutUsableOrigin_ShouldNotApplyArea(RoadmapOriginMode mode)
    {
        DishCandidateFilter? captured = null;
        SetupOwned(new RoadmapSuggestionContext(mode, null, null, []));
        _dishRepository.Setup(item => item.GetCandidateIdsAsync(
                It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback<DishCandidateFilter, int, CancellationToken>((filter, _, _) => captured = filter)
            .ReturnsAsync([]);
        _recommendationService.Setup(item => item.RecommendAsync(
                TravelerId, It.IsAny<IReadOnlyList<int>>(), 200,
                RecommendationContext.RoadmapManual, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(null, [], []));
        CardsFor();

        await CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "Huế", RadiusKm = 20 }, CancellationToken.None);

        captured!.Area.Should().BeNull();
    }

    // TEST-04: Ignore duplicate or unexpected ids returned by the recommendation boundary.
    /// <summary>Verifies RM-06 never exposes a dish that did not pass its candidate filters.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenRecoReturnsUnexpectedIds_ShouldKeepDistinctCandidatesOnly()
    {
        SetupOwned(new RoadmapSuggestionContext(RoadmapOriginMode.None, null, null, []));
        Candidates(1, 2);
        Ranked(Guid.NewGuid(), 2, 999, 2, 1);
        CardsFor(1, 2, 999);

        var result = await CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "Huế" }, CancellationToken.None);

        result.Items.Select(item => (item.Id, item.Rank)).Should().Equal((2, 1), (1, 2));
        result.NextCursor.Should().BeNull();
    }

    // TEST-05: Reuse the cached ranked list on later pages without filtering or ranking again.
    /// <summary>Verifies stable ranks and the single-call cursor contract.</summary>
    [Fact]
    public async Task ExecuteAsync_WithCursor_ShouldReuseScopedCachedList()
    {
        SetupOwned(new RoadmapSuggestionContext(RoadmapOriginMode.None, null, null, []));
        Candidates(1, 2, 3);
        Ranked(Guid.NewGuid(), 3, 1, 2);
        CardsFor(1, 2, 3);
        var first = await CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "Huế", Limit = 2 }, CancellationToken.None);

        var second = await CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "ignored", Cursor = first.NextCursor }, CancellationToken.None);

        second.Items.Select(item => (item.Id, item.Rank)).Should().Equal((2, 3));
        second.NextCursor.Should().BeNull();
        _dishRepository.Verify(item => item.GetCandidateIdsAsync(
            It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _recommendationService.Verify(item => item.RecommendAsync(
            It.IsAny<int?>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<int?>(),
            It.IsAny<RecommendationContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-06: Reject malformed, unknown, cross-roadmap, and cross-user cursors.
    /// <summary>Verifies cached suggestions cannot escape their authenticated roadmap scope.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidOrForeignCursor_ShouldThrowInvalidCursor()
    {
        SetupOwned(new RoadmapSuggestionContext(RoadmapOriginMode.None, null, null, []));
        Candidates(1, 2);
        Ranked(Guid.NewGuid(), 1, 2);
        CardsFor(1, 2);
        var cursor = (await CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "Huế", Limit = 1 }, CancellationToken.None)).NextCursor;

        foreach (var (roadmapId, userId, value) in new[]
                 {
                     (RoadmapId, TravelerId, "bad-cursor"),
                     (RoadmapId + 1, TravelerId, cursor!),
                     (RoadmapId, TravelerId + 1, cursor!)
                 })
        {
            _currentUser.SetupGet(item => item.UserId).Returns(userId);
            _roadmapRepository.Setup(item => item.GetSuggestionContextAsync(
                    roadmapId, userId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RoadmapSuggestionContext(RoadmapOriginMode.None, null, null, []));
            var act = () => CreateUseCase().ExecuteAsync(roadmapId,
                new GetRoadmapSuggestionsRequestDto { Province = "Huế", Cursor = value }, CancellationToken.None);
            (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("INVALID_CURSOR");
        }
    }

    // TEST-07: Return ROADMAP_NOT_FOUND for a missing or foreign roadmap before ranking.
    /// <summary>Verifies RM-06 ownership hiding and absence behavior.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenRoadmapIsNotOwned_ShouldThrowRoadmapNotFound()
    {
        _currentUser.SetupGet(item => item.UserId).Returns(TravelerId);
        _roadmapRepository.Setup(item => item.GetSuggestionContextAsync(
                RoadmapId, TravelerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RoadmapSuggestionContext?)null);

        var act = () => CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "Huế" }, CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("ROADMAP_NOT_FOUND");
        _dishRepository.VerifyNoOtherCalls();
    }

    // TEST-08: Reject an unauthenticated caller without reading roadmap data.
    /// <summary>Verifies Application enforces RM-06 authentication independently of the controller.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        _currentUser.SetupGet(item => item.UserId).Returns((int?)null);
        var act = () => CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "Huế" }, CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
        _roadmapRepository.VerifyNoOtherCalls();
    }

    // TEST-09: Validate before reading identity, roadmap, dishes, or reco.
    /// <summary>Verifies the coding-rule validation-first invariant.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidRequest_ShouldThrowValidationFirst()
    {
        var act = () => CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "", Limit = 0 }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        _roadmapRepository.VerifyNoOtherCalls();
        _dishRepository.VerifyNoOtherCalls();
        _recommendationService.VerifyNoOtherCalls();
    }

    /// <summary>Creates RM-06 with real validation and deterministic in-memory boundaries.</summary>
    private GetRoadmapSuggestionsUseCase CreateUseCase() => new(
        new GetRoadmapSuggestionsRequestDtoValidator(),
        _roadmapRepository.Object,
        _dishRepository.Object,
        _recommendationService.Object,
        _cache,
        _currentUser.Object);

    /// <summary>Configures an authenticated traveler and one owned roadmap context.</summary>
    private void SetupOwned(RoadmapSuggestionContext context)
    {
        _currentUser.SetupGet(item => item.UserId).Returns(TravelerId);
        _roadmapRepository.Setup(item => item.GetSuggestionContextAsync(
                RoadmapId, TravelerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);
    }

    /// <summary>Configures the repository candidate ids returned before ranking.</summary>
    private void Candidates(params int[] ids) =>
        _dishRepository.Setup(item => item.GetCandidateIdsAsync(
                It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids);

    /// <summary>Configures the deterministic RECO-05 ordering.</summary>
    private void Ranked(Guid? requestId, params int[] ids) =>
        _recommendationService.Setup(item => item.RecommendAsync(
                TravelerId, It.IsAny<IReadOnlyList<int>>(), 200,
                RecommendationContext.RoadmapManual, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(requestId, ids, []));

    /// <summary>Returns cards for requested ids that remain public at page-load time.</summary>
    private void CardsFor(params int[] availableIds) =>
        _dishRepository.Setup(item => item.GetCardDishesAsync(
                It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<int> ids, CancellationToken _) =>
                ids.Where(availableIds.Contains).Select(Dish).ToArray());

    /// <summary>Builds the minimum entity graph needed by the shared DishCard mapper.</summary>
    private static Dish Dish(int id) => new()
    {
        Id = id,
        Name = $"Dish {id}",
        Price = 30_000,
        DishForm = DishForm.Dry,
        Restaurant = new Domain.Entities.Restaurant { Id = 7, Name = "Restaurant" }
    };

    private sealed class InMemoryCache : IRankedDishListCache
    {
        private readonly Dictionary<Guid, RankedDishList> _lists = [];

        /// <summary>Stores a list under its opaque cursor key.</summary>
        public void Store(RankedDishList list) => _lists[list.Key] = list;

        /// <summary>Returns a stored list or null for an unknown key.</summary>
        public RankedDishList? Get(Guid key) => _lists.GetValueOrDefault(key);
    }
}
