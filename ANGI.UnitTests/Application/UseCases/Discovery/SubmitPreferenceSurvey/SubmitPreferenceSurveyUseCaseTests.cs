using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.UseCases.Discovery.SubmitPreferenceSurvey;
using ANGI.Application.UseCases.Validators.Discovery;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Discovery.SubmitPreferenceSurvey;

public sealed class SubmitPreferenceSurveyUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 2, 30, 0, TimeSpan.Zero);
    private const int TravelerId = 12;

    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IRecommendationService> _recommendationService = new();

    public SubmitPreferenceSurveyUseCaseTests()
    {
        _currentUserService.SetupGet(x => x.UserId).Returns(TravelerId);
    }

    private SubmitPreferenceSurveyUseCase CreateUseCase() => new(
        new SubmitPreferenceSurveyRequestDtoValidator(),
        _currentUserService.Object,
        _recommendationService.Object,
        new FixedTimeProvider(Now));

    private static SubmitPreferenceSurveyRequestDto ValidRequest() => new()
    {
        Answers =
        [
            new SurveyAnswerDto { OptionCode = "broth_rich", ScaleValue = 0.8m },
            new SurveyAnswerDto { OptionCode = "spicy_hot" }
        ]
    };

    // TEST-01: Forward every answer of the caller to RECO-03 and return the counts reco stored.
    [Fact]
    public async Task ExecuteAsync_WithValidAnswers_ShouldForwardThemToRecoAndReturnItsResult()
    {
        IReadOnlyList<SurveyAnswer>? sent = null;
        _recommendationService
            .Setup(x => x.SubmitSurveyAsync(
                TravelerId,
                It.IsAny<IReadOnlyList<SurveyAnswer>>(),
                Now.UtcDateTime,
                It.IsAny<CancellationToken>()))
            .Callback<int, IReadOnlyList<SurveyAnswer>, DateTime?, CancellationToken>((_, answers, _, _) => sent = answers)
            .ReturnsAsync(new SurveySubmissionResult(2, true, Now.UtcDateTime));

        var result = await CreateUseCase().ExecuteAsync(ValidRequest(), CancellationToken.None);

        result.Should().BeEquivalentTo(new SubmitPreferenceSurveyResponseDto
        {
            Completed = true,
            AnswerCount = 2,
            HasVector = true
        });
        sent.Should().Equal(new SurveyAnswer("broth_rich", 0.8m), new SurveyAnswer("spicy_hot"));
    }

    // TEST-02: Let the documented reco outcomes (409, 400, 503) reach the client unchanged.
    [Fact]
    public async Task ExecuteAsync_WhenRecoRejectsTheSurvey_ShouldPropagateTheDocumentedError()
    {
        _recommendationService
            .Setup(x => x.SubmitSurveyAsync(
                It.IsAny<int>(),
                It.IsAny<IReadOnlyList<SurveyAnswer>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConflictException("SURVEY_ALREADY_SUBMITTED", "Bạn đã làm hoặc đã bỏ qua khảo sát sở thích."));

        var act = () => CreateUseCase().ExecuteAsync(ValidRequest(), CancellationToken.None);

        (await act.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("SURVEY_ALREADY_SUBMITTED");
    }

    // TEST-03: Reject invalid answers before calling reco.
    [Fact]
    public async Task ExecuteAsync_WithNoAnswers_ShouldThrowValidationExceptionWithoutCallingReco()
    {
        var act = () => CreateUseCase().ExecuteAsync(new SubmitPreferenceSurveyRequestDto(), CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        _recommendationService.VerifyNoOtherCalls();
    }

    // TEST-04: Refuse a request without a user id in the token.
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        _currentUserService.SetupGet(x => x.UserId).Returns((int?)null);

        var act = () => CreateUseCase().ExecuteAsync(ValidRequest(), CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
        _recommendationService.VerifyNoOtherCalls();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
