using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.UseCases.Discovery.RecordImpressions;
using ANGI.Application.UseCases.Validators.Discovery;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Discovery.RecordImpressions;

/// <summary>Verifies RecordImpressionsUseCase forwards the caller's impressions to reco with a trustworthy timestamp.</summary>
public sealed class RecordImpressionsUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 2, 15, 0, TimeSpan.Zero);
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IRecommendationService> _recommendationService = new();

    public RecordImpressionsUseCaseTests()
    {
        _currentUserService.SetupGet(x => x.UserId).Returns(12);
    }

    /// <summary>Creates the use case with the real validator, a fixed clock and the shared mocks.</summary>
    private RecordImpressionsUseCase CreateUseCase() => new(
        new RecordImpressionsRequestDtoValidator(),
        _currentUserService.Object,
        _recommendationService.Object,
        new FixedTimeProvider(Now));

    // TEST-01: Forward the impressions of the caller to RECO-06, with the server time when shownAt is omitted.
    /// <summary>Ensures impressions without shownAt are stamped with server time before reaching reco.</summary>
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
    /// <summary>Ensures the time the app actually showed the dish is preserved.</summary>
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
    /// <summary>Ensures invalid impressions never reach reco.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidBody_ShouldNotCallReco()
    {
        var act = () => CreateUseCase().ExecuteAsync(new RecordImpressionsRequestDto { DishIds = [] }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        _recommendationService.VerifyNoOtherCalls();
    }

    /// <summary>Clock fixed at one instant so the default shownAt can be asserted exactly.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        /// <summary>Returns the fixed instant supplied to the constructor.</summary>
        public override DateTimeOffset GetUtcNow() => now;
    }
}
