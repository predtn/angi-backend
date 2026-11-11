using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.Common.Interfaces.UseCases.Discovery;

/// <summary>Submits the authenticated Traveler's preference survey to reco (PREF-02).</summary>
public interface ISubmitPreferenceSurveyUseCase
{
    /// <summary>
    /// Validates the answers and forwards them to RECO-03. Raises 409 SURVEY_ALREADY_SUBMITTED,
    /// 400 UNKNOWN_OPTION_CODE or 503 SERVICE_UNAVAILABLE as reco answers.
    /// </summary>
    Task<SubmitPreferenceSurveyResponseDto> ExecuteAsync(SubmitPreferenceSurveyRequestDto request, CancellationToken ct);
}
