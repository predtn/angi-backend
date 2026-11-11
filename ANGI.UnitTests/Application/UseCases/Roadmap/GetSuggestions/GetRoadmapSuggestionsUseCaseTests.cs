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

/// <summary>Verifies GetRoadmapSuggestionsUseCase filters, ranks and pages dish suggestions for a roadmap.</summary>
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
        // STEP-01: Create requestId through Guid.NewGuid, declare captured to capture a call argument and configure the stubs through SetupOwned.
        var requestId = Guid.NewGuid();
        DishCandidateFilter? captured = null;
        SetupOwned(new RoadmapSuggestionContext(RoadmapOriginMode.Pinned, 11, 107, [9, 10]));
        // STEP-02: Stub dishRepository.GetCandidateIdsAsync and recommendationService.RecommendAsync.
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
        // STEP-03: Call CardsFor.
        CardsFor(1, 2, 3);

        // STEP-04: Run CreateUseCase().ExecuteAsync with RoadmapId and GetRoadmapSuggestionsRequestDto { Province, DayId, MealSlot, ... } and keep result.
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

        // STEP-05: Assert captured is equivalent to a new DishCandidateFilter, result.RequestId is requestId, result.Items.Select(filtered) equals (3, 1) and 1 more.
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
        // STEP-01: Declare captured to capture a call argument and configure the stubs through SetupOwned.
        DishCandidateFilter? captured = null;
        SetupOwned(new RoadmapSuggestionContext(RoadmapOriginMode.Pinned, 16.1m, 108.3m, []));
        // STEP-02: Stub dishRepository.GetCandidateIdsAsync and recommendationService.RecommendAsync.
        _dishRepository.Setup(item => item.GetCandidateIdsAsync(
                It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback<DishCandidateFilter, int, CancellationToken>((filter, _, _) => captured = filter)
            .ReturnsAsync([1]);
        _recommendationService.Setup(item => item.RecommendAsync(
                TravelerId, It.IsAny<IReadOnlyList<int>>(), 200,
                RecommendationContext.RoadmapManual, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(null, [1], []));
        // STEP-03: Call CardsFor.
        CardsFor(1);

        // STEP-04: Run CreateUseCase().ExecuteAsync with RoadmapId and GetRoadmapSuggestionsRequestDto { Province = the expected text } and keep result.
        var result = await CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "Đà Nẵng" }, CancellationToken.None);

        // STEP-05: Assert captured.Area is a new NearbyArea and result.RequestId is null.
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
        // STEP-01: Declare captured to capture a call argument and configure the stubs through SetupOwned.
        DishCandidateFilter? captured = null;
        SetupOwned(new RoadmapSuggestionContext(mode, null, null, []));
        // STEP-02: Stub dishRepository.GetCandidateIdsAsync and recommendationService.RecommendAsync.
        _dishRepository.Setup(item => item.GetCandidateIdsAsync(
                It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback<DishCandidateFilter, int, CancellationToken>((filter, _, _) => captured = filter)
            .ReturnsAsync([]);
        _recommendationService.Setup(item => item.RecommendAsync(
                TravelerId, It.IsAny<IReadOnlyList<int>>(), 200,
                RecommendationContext.RoadmapManual, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(null, [], []));
        // STEP-03: Call CardsFor.
        CardsFor();

        // STEP-04: Run CreateUseCase().ExecuteAsync with RoadmapId and GetRoadmapSuggestionsRequestDto { Province = the expected text, RadiusKm = 20 }.
        await CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "Huế", RadiusKm = 20 }, CancellationToken.None);

        // STEP-05: Assert captured.Area is null.
        captured!.Area.Should().BeNull();
    }

    // TEST-04: Ignore duplicate or unexpected ids returned by the recommendation boundary.
    /// <summary>Verifies RM-06 never exposes a dish that did not pass its candidate filters.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenRecoReturnsUnexpectedIds_ShouldKeepDistinctCandidatesOnly()
    {
        // STEP-01: Configure the stubs through SetupOwned, call Candidates, call Ranked and call CardsFor.
        SetupOwned(new RoadmapSuggestionContext(RoadmapOriginMode.None, null, null, []));
        Candidates(1, 2);
        Ranked(Guid.NewGuid(), 2, 999, 2, 1);
        CardsFor(1, 2, 999);

        // STEP-02: Run CreateUseCase().ExecuteAsync with RoadmapId and GetRoadmapSuggestionsRequestDto { Province = the expected text } and keep result.
        var result = await CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "Huế" }, CancellationToken.None);

        // STEP-03: Assert result.Items.Select(filtered) equals (2, 1) and result.NextCursor is null.
        result.Items.Select(item => (item.Id, item.Rank)).Should().Equal((2, 1), (1, 2));
        result.NextCursor.Should().BeNull();
    }

    // TEST-05: Reuse the cached ranked list on later pages without filtering or ranking again.
    /// <summary>Verifies stable ranks and the single-call cursor contract.</summary>
    [Fact]
    public async Task ExecuteAsync_WithCursor_ShouldReuseScopedCachedList()
    {
        // STEP-01: Configure the stubs through SetupOwned, call Candidates, call Ranked and call CardsFor.
        SetupOwned(new RoadmapSuggestionContext(RoadmapOriginMode.None, null, null, []));
        Candidates(1, 2, 3);
        Ranked(Guid.NewGuid(), 3, 1, 2);
        CardsFor(1, 2, 3);
        // STEP-02: Run CreateUseCase().ExecuteAsync with RoadmapId and GetRoadmapSuggestionsRequestDto { Province = the expected text, Limit = 2 } and keep first.
        var first = await CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "Huế", Limit = 2 }, CancellationToken.None);

        // STEP-03: Run CreateUseCase().ExecuteAsync with RoadmapId and GetRoadmapSuggestionsRequestDto { Province = "ignored", Cursor = first.NextCursor } and keep second.
        var second = await CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "ignored", Cursor = first.NextCursor }, CancellationToken.None);

        // STEP-04: Assert second.Items.Select(filtered) equals (2, 3) and second.NextCursor is null.
        second.Items.Select(item => (item.Id, item.Rank)).Should().Equal((2, 3));
        second.NextCursor.Should().BeNull();
        // STEP-05: Verify GetCandidateIdsAsync is called once and RecommendAsync is called once.
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
        // STEP-01: Configure the stubs through SetupOwned, call Candidates, call Ranked and call CardsFor.
        SetupOwned(new RoadmapSuggestionContext(RoadmapOriginMode.None, null, null, []));
        Candidates(1, 2);
        Ranked(Guid.NewGuid(), 1, 2);
        CardsFor(1, 2);
        // STEP-02: Run CreateUseCase().ExecuteAsync with RoadmapId and GetRoadmapSuggestionsRequestDto { Province = the expected text, Limit = 1 } and keep cursor.
        var cursor = (await CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "Huế", Limit = 1 }, CancellationToken.None)).NextCursor;

        // STEP-03: Assert act throws BadRequestException and the thrown exception.ErrorCode is "INVALID_CURSOR".
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
        // STEP-01: Stub currentUser.UserId and roadmapRepository.GetSuggestionContextAsync.
        _currentUser.SetupGet(item => item.UserId).Returns(TravelerId);
        _roadmapRepository.Setup(item => item.GetSuggestionContextAsync(
                RoadmapId, TravelerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RoadmapSuggestionContext?)null);

        // STEP-02: Wrap CreateUseCase().ExecuteAsync with RoadmapId and GetRoadmapSuggestionsRequestDto { Province = the expected text } in act so its outcome can be asserted.
        var act = () => CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "Huế" }, CancellationToken.None);

        // STEP-03: Assert act throws NotFoundException and the thrown exception.ErrorCode is "ROADMAP_NOT_FOUND".
        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("ROADMAP_NOT_FOUND");
        // STEP-04: Verify dishRepository receives no other calls.
        _dishRepository.VerifyNoOtherCalls();
    }

    // TEST-08: Reject an unauthenticated caller without reading roadmap data.
    /// <summary>Verifies Application enforces RM-06 authentication independently of the controller.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        // STEP-01: Stub currentUser.UserId.
        _currentUser.SetupGet(item => item.UserId).Returns((int?)null);
        // STEP-02: Wrap CreateUseCase().ExecuteAsync with RoadmapId and GetRoadmapSuggestionsRequestDto { Province = the expected text } in act so its outcome can be asserted.
        var act = () => CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "Huế" }, CancellationToken.None);

        // STEP-03: Assert act throws UnauthorizedException and the thrown exception.ErrorCode is "UNAUTHORIZED".
        (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
        // STEP-04: Verify roadmapRepository receives no other calls.
        _roadmapRepository.VerifyNoOtherCalls();
    }

    // TEST-09: Validate before reading identity, roadmap, dishes, or reco.
    /// <summary>Verifies the coding-rule validation-first invariant.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidRequest_ShouldThrowValidationFirst()
    {
        // STEP-01: Wrap CreateUseCase().ExecuteAsync with RoadmapId and GetRoadmapSuggestionsRequestDto { Province = an empty string, Limit = 0 } in act so its outcome can be asserted.
        var act = () => CreateUseCase().ExecuteAsync(RoadmapId,
            new GetRoadmapSuggestionsRequestDto { Province = "", Limit = 0 }, CancellationToken.None);

        // STEP-02: Assert act throws ValidationException.
        await act.Should().ThrowAsync<ValidationException>();
        // STEP-03: Verify roadmapRepository receives no other calls, dishRepository receives no other calls and recommendationService receives no other calls.
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

    /// <summary>Dictionary-backed ranked list cache so paging can be tested without Redis.</summary>
    private sealed class InMemoryCache : IRankedDishListCache
    {
        private readonly Dictionary<Guid, RankedDishList> _lists = [];

        /// <summary>Stores a list under its opaque cursor key.</summary>
        public void Store(RankedDishList list) => _lists[list.Key] = list;

        /// <summary>Returns a stored list or null for an unknown key.</summary>
        public RankedDishList? Get(Guid key) => _lists.GetValueOrDefault(key);
    }
}
