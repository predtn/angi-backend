using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.UseCases.Discovery.RollDish;
using ANGI.Application.UseCases.Validators.Discovery;
using ANGI.Domain.Entities;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Discovery.RollDish;

public sealed class RollDishUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 3, 0, 0, TimeSpan.Zero);
    private const int UserId = 12;

    private readonly Mock<IDishRepository> _dishRepository = new();
    private readonly Mock<IRecommendationService> _recommendationService = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly List<DishRoll> _rolls = [];

    public RollDishUseCaseTests()
    {
        _currentUserService.SetupGet(x => x.UserId).Returns(UserId);
        _currentUserService.SetupGet(x => x.Role).Returns("TRAVELER");
        _dishRepository.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([1, 2, 3]);
        _dishRepository.Setup(x => x.GetPublicDetailAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => Dish(id));
        _dishRepository.Setup(x => x.AddRoll(It.IsAny<DishRoll>())).Callback<DishRoll>(_rolls.Add);
        // The database assigns dish_rolls.id on save.
        _unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _rolls.ForEach(roll => roll.Id = 4410))
            .ReturnsAsync(1);
    }

    private RollDishUseCase CreateUseCase(Random random) => new(
        new RollDishRequestDtoValidator(),
        _dishRepository.Object,
        _recommendationService.Object,
        _currentUserService.Object,
        _unitOfWork.Object,
        new FixedTimeProvider(Now),
        random);

    private void RecoReturns(Guid? requestId, int[] dishIds, double[]? scores) =>
        _recommendationService.Setup(x => x.RecommendAsync(
                It.IsAny<int?>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<int?>(), It.IsAny<RecommendationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(requestId, dishIds, [], scores));

    private static Dish Dish(int id) => new()
    {
        Id = id,
        Name = $"Món {id}",
        Restaurant = new Domain.Entities.Restaurant { Id = 7, Name = "Quán", Latitude = 16.0678m, Longitude = 108.2208m }
    };

    // TEST-01: Build the candidate filter from the request (3 km default radius), ask reco for its top 10 with context = roll.
    [Fact]
    public async Task ExecuteAsync_ShouldFilterCandidatesAndAskRecoForTopTenRoll()
    {
        RecoReturns(Guid.NewGuid(), [2], [0.5]);
        DishCandidateFilter? filter = null;
        _dishRepository.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback<DishCandidateFilter, int, CancellationToken>((value, _, _) => filter = value)
            .ReturnsAsync([1, 2, 3]);

        await CreateUseCase(new StubRandom(0, 0)).ExecuteAsync(new RollDishRequestDto
        {
            Province = "Đà Nẵng",
            TagIds = [9],
            ExcludeTagIds = [12],
            MealTime = "breakfast",
            MaxPrice = 60000,
            ExcludeDishIds = [88, 91],
            Latitude = 16.0678m,
            Longitude = 108.2208m
        }, CancellationToken.None);

        filter.Should().BeEquivalentTo(new
        {
            Province = "Đà Nẵng",
            AnyTagIds = new short[] { 9 },
            TagIds = Array.Empty<short>(),
            ExcludeTagIds = new short[] { 12 },
            MealTimeCode = "breakfast",
            MaxPrice = 60000m,
            ExcludeDishIds = new[] { 88, 91 },
            Area = new NearbyArea(16.0678m, 108.2208m, 3)
        });
        _recommendationService.Verify(x => x.RecommendAsync(
            UserId, It.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 1, 2, 3 })), 10, RecommendationContext.Roll,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: The draw is weighted by recommend_score: with scores 1, 3 the point 0.5 of 4 lands on the second dish.
    [Theory]
    [InlineData(0.2, 1)]
    [InlineData(0.5, 2)]
    [InlineData(0.99, 2)]
    public async Task ExecuteAsync_ShouldPickProportionallyToScores(double draw, int expectedRank)
    {
        RecoReturns(Guid.NewGuid(), [5, 6], [1, 3]);

        var result = await CreateUseCase(new StubRandom(draw, 0)).ExecuteAsync(new RollDishRequestDto(), CancellationToken.None);

        result.Dish.Rank.Should().Be(expectedRank);
        result.Dish.Id.Should().Be(expectedRank == 1 ? 5 : 6);
    }

    // TEST-03: Without scores (reco fallback) or with no positive score, every dish of the top list is equally likely.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_WithoutUsableScores_ShouldPickUniformly(bool zeroScores)
    {
        RecoReturns(zeroScores ? Guid.NewGuid() : null, [5, 6, 7], zeroScores ? [0, 0, 0] : null);
        var random = new StubRandom(0, nextIndex: 2);

        var result = await CreateUseCase(random).ExecuteAsync(new RollDishRequestDto(), CancellationToken.None);

        random.LastMaxValue.Should().Be(3);
        result.Dish.Id.Should().Be(7);
        result.Dish.Rank.Should().Be(3);
    }

    // TEST-04: Store the roll with reco's request id, report only the rolled dish to reco and return the stored id.
    [Fact]
    public async Task ExecuteAsync_ShouldStoreRollAndReportImpression()
    {
        var requestId = Guid.NewGuid();
        RecoReturns(requestId, [5, 6], [1, 1]);

        var result = await CreateUseCase(new StubRandom(0.9, 0)).ExecuteAsync(new RollDishRequestDto(), CancellationToken.None);

        _rolls.Should().ContainSingle().Which.Should().BeEquivalentTo(new { UserId, DishId = 6, RecoRequestId = (Guid?)requestId });
        result.RollId.Should().Be(4410);
        result.RequestId.Should().Be(requestId);
        _recommendationService.Verify(x => x.RecordImpressionsAsync(
            UserId, requestId, It.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 6 })), Now.UtcDateTime,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-05: When reco failed (request id null) the roll is still stored but nothing is reported to reco.
    [Fact]
    public async Task ExecuteAsync_WithoutRequestId_ShouldNotReportImpression()
    {
        RecoReturns(null, [5], null);

        var result = await CreateUseCase(new StubRandom(0, 0)).ExecuteAsync(new RollDishRequestDto(), CancellationToken.None);

        result.RequestId.Should().BeNull();
        _rolls.Should().ContainSingle().Which.RecoRequestId.Should().BeNull();
        _recommendationService.Verify(x => x.RecordImpressionsAsync(
            It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-06: distanceKm is the rounded distance to the sent point, and null without coordinates.
    [Fact]
    public async Task ExecuteAsync_ShouldReturnDistanceOnlyWithCoordinates()
    {
        RecoReturns(null, [5], null);
        var useCase = CreateUseCase(new StubRandom(0, 0));

        var near = await useCase.ExecuteAsync(new RollDishRequestDto { Latitude = 16.0768m, Longitude = 108.2208m }, CancellationToken.None);
        var anywhere = await useCase.ExecuteAsync(new RollDishRequestDto(), CancellationToken.None);

        near.Dish.Restaurant.DistanceKm.Should().Be(1.0m);
        anywhere.Dish.Restaurant.DistanceKm.Should().BeNull();
    }

    // TEST-07: No candidate, an empty reco list, or a dish gone before loading all end in 404 NO_CANDIDATE without a roll.
    [Theory]
    [InlineData("candidates")]
    [InlineData("reco")]
    [InlineData("detail")]
    public async Task ExecuteAsync_WithNothingToRoll_ShouldThrowNoCandidate(string emptyStep)
    {
        RecoReturns(null, emptyStep == "reco" ? [] : [5], null);
        if (emptyStep == "candidates")
        {
            _dishRepository.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
        }

        if (emptyStep == "detail")
        {
            _dishRepository.Setup(x => x.GetPublicDetailAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync((Dish?)null);
        }

        var act = () => CreateUseCase(new StubRandom(0, 0)).ExecuteAsync(new RollDishRequestDto(), CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("NO_CANDIDATE");
        _rolls.Should().BeEmpty();
        _unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-08: Reject an invalid request before touching the database or reco.
    [Fact]
    public async Task ExecuteAsync_WithInvalidRequest_ShouldThrowValidation()
    {
        var act = () => CreateUseCase(new StubRandom(0, 0)).ExecuteAsync(new RollDishRequestDto { MealTime = "brunch" }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        _dishRepository.Verify(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Returns fixed draws and remembers the range of the last uniform draw.</summary>
    private sealed class StubRandom(double nextDouble, int nextIndex) : Random
    {
        public int? LastMaxValue { get; private set; }

        public override double NextDouble() => nextDouble;

        public override int Next(int maxValue)
        {
            LastMaxValue = maxValue;
            return nextIndex;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
