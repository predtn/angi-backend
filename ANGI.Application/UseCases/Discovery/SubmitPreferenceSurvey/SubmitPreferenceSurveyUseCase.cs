using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.DTOs.Discovery;
using FluentValidation;

namespace ANGI.Application.UseCases.Discovery.SubmitPreferenceSurvey;

/// <summary>
/// Forwards the Traveler's survey answers to reco (PREF-02 → RECO-03). The backend stores nothing: reco is the only
/// place that keeps survey answers.
/// </summary>
public sealed class SubmitPreferenceSurveyUseCase : ISubmitPreferenceSurveyUseCase
{
    private const string UnauthorizedCode = "UNAUTHORIZED";
    private const string UnauthorizedMessage = "Yêu cầu đăng nhập.";

    private readonly IValidator<SubmitPreferenceSurveyRequestDto> _validator;
    private readonly ICurrentUserService _currentUserService;
    private readonly IRecommendationService _recommendationService;
    private readonly TimeProvider _timeProvider;

    public SubmitPreferenceSurveyUseCase(
        IValidator<SubmitPreferenceSurveyRequestDto> validator,
        ICurrentUserService currentUserService,
        IRecommendationService recommendationService,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _currentUserService = currentUserService;
        _recommendationService = recommendationService;
        _timeProvider = timeProvider;
    }

    public async Task<SubmitPreferenceSurveyResponseDto> ExecuteAsync(
        SubmitPreferenceSurveyRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException(UnauthorizedCode, UnauthorizedMessage);

        var answers = request.Answers
            .Select(answer => new SurveyAnswer(answer.OptionCode, answer.ScaleValue))
            .ToArray();

        var result = await _recommendationService.SubmitSurveyAsync(
            userId,
            answers,
            _timeProvider.GetUtcNow().UtcDateTime,
            ct);

        return new SubmitPreferenceSurveyResponseDto
        {
            Completed = true,
            AnswerCount = result.AnswerCount,
            HasVector = result.HasVector
        };
    }
}
