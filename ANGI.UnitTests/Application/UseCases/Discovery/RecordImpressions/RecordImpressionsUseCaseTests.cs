using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.UseCases.Discovery.RecordImpressions;
using ANGI.Application.UseCases.Validators.Discovery;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Discovery.RecordImpressions;

public sealed class RecordImpressionsUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 2, 15, 0, TimeSpan.Zero);
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IRecommendationService> _recommendationService = new();

    public RecordImpressionsUseCaseTests()
    {
        _currentUserService.SetupGet(x => x.UserId).Returns(12);
    }

    private RecordImpressionsUseCase CreateUseCase() => new(
        new RecordImpressionsRequestDtoValidator(),
        _currentUserService.Object,
        _recommendationService.Object,
        new FixedTimeProvider(Now));

    // TEST-01: Forward the impressions of the caller to RECO-06, with the server time when shownAt is omitted.
    [Fact]
    public async Task ExecuteAsync_ShouldForwardToRecoWithServerTimeByDefault()
    {
        var requestId = Guid.NewGuid();

        var result = await CreateUseCase().ExecuteAsync(
            new RecordImpressionsRequestDto { RequestId = requestId, DishIds = [105, 120] },
            CancellationToken.None);

        result.Accepted.Should().BeTrue();
        _recommendationService.Verify(x => x.RecordImpressionsAsync(
            12, requestId, It.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 105, 120 })), Now.UtcDateTime,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Keep the shownAt the app sent.
    [Fact]
    public async Task ExecuteAsync_WithShownAt_ShouldForwardIt()
    {
        var shownAt = new DateTime(2026, 10, 2, 2, 15, 0, DateTimeKind.Utc);

        await CreateUseCase().ExecuteAsync(
            new RecordImpressionsRequestDto { RequestId = Guid.NewGuid(), DishIds = [105], ShownAt = shownAt },
            CancellationToken.None);

        _recommendationService.Verify(x => x.RecordImpressionsAsync(
            12, It.IsAny<Guid>(), It.IsAny<IReadOnlyList<int>>(), shownAt, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Reject an invalid body before calling reco.
    [Fact]
    public async Task ExecuteAsync_WithInvalidBody_ShouldNotCallReco()
    {
        var act = () => CreateUseCase().ExecuteAsync(new RecordImpressionsRequestDto { DishIds = [] }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        _recommendationService.VerifyNoOtherCalls();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
