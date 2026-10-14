using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.UseCases.Discovery.SkipPreferenceSurvey;

/// <summary>
/// Skips the survey by sending RECO-03 with no answers (PREF-03). Reco still sets survey_at, so the survey cannot be
/// taken afterwards.
/// </summary>
public sealed class SkipPreferenceSurveyUseCase : ISkipPreferenceSurveyUseCase
{
    private const string UnauthorizedCode = "UNAUTHORIZED";
    private const string UnauthorizedMessage = "Yêu cầu đăng nhập.";

    private readonly ICurrentUserService _currentUserService;
    private readonly IRecommendationService _recommendationService;
    private readonly TimeProvider _timeProvider;

    public SkipPreferenceSurveyUseCase(
        ICurrentUserService currentUserService,
        IRecommendationService recommendationService,
        TimeProvider timeProvider)
    {
        _currentUserService = currentUserService;
        _recommendationService = recommendationService;
        _timeProvider = timeProvider;
    }

    public async Task<SkipPreferenceSurveyResponseDto> ExecuteAsync(CancellationToken ct)
    {
        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException(UnauthorizedCode, UnauthorizedMessage);

        await _recommendationService.SubmitSurveyAsync(
            userId,
            Array.Empty<SurveyAnswer>(),
            _timeProvider.GetUtcNow().UtcDateTime,
            ct);

        return new SkipPreferenceSurveyResponseDto
        {
            Completed = true,
            Skipped = true
        };
    }
}
