using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.UseCases.Discovery.SkipPreferenceSurvey;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Discovery.SkipPreferenceSurvey;

public sealed class SkipPreferenceSurveyUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 2, 30, 0, TimeSpan.Zero);
    private const int TravelerId = 12;

    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IRecommendationService> _recommendationService = new();

    public SkipPreferenceSurveyUseCaseTests()
    {
        _currentUserService.SetupGet(x => x.UserId).Returns(TravelerId);
    }

    private SkipPreferenceSurveyUseCase CreateUseCase() => new(
        _currentUserService.Object,
        _recommendationService.Object,
        new FixedTimeProvider(Now));

    // TEST-01: Skip by sending RECO-03 an empty answer list for the caller.
    [Fact]
    public async Task ExecuteAsync_ShouldSendNoAnswersToRecoAndReturnSkipped()
    {
        _recommendationService
            .Setup(x => x.SubmitSurveyAsync(
                TravelerId,
                It.Is<IReadOnlyList<SurveyAnswer>>(answers => answers.Count == 0),
                Now.UtcDateTime,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SurveySubmissionResult(0, false, Now.UtcDateTime));

        var result = await CreateUseCase().ExecuteAsync(CancellationToken.None);

        result.Should().BeEquivalentTo(new SkipPreferenceSurveyResponseDto { Completed = true, Skipped = true });
        _recommendationService.VerifyAll();
    }

    // TEST-02: Return 503 when reco cannot store the skip, so the app can try again.
    [Fact]
    public async Task ExecuteAsync_WhenRecoIsUnavailable_ShouldPropagateServiceUnavailable()
    {
        _recommendationService
            .Setup(x => x.SubmitSurveyAsync(
                It.IsAny<int>(),
                It.IsAny<IReadOnlyList<SurveyAnswer>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceUnavailableException("SERVICE_UNAVAILABLE", "Dịch vụ gợi ý đang bận, vui lòng thử lại."));

        var act = () => CreateUseCase().ExecuteAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<ServiceUnavailableException>()).Which.ErrorCode.Should().Be("SERVICE_UNAVAILABLE");
    }

    // TEST-03: Refuse a request without a user id in the token.
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        _currentUserService.SetupGet(x => x.UserId).Returns((int?)null);

        var act = () => CreateUseCase().ExecuteAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
        _recommendationService.VerifyNoOtherCalls();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
