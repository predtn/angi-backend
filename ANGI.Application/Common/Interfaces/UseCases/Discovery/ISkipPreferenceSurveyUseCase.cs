using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.Common.Interfaces.UseCases.Discovery;

/// <summary>Skips the authenticated Traveler's preference survey for good (PREF-03).</summary>
public interface ISkipPreferenceSurveyUseCase
{
    /// <summary>Sends RECO-03 with no answers. Raises 409 SURVEY_ALREADY_SUBMITTED or 503 SERVICE_UNAVAILABLE.</summary>
    Task<SkipPreferenceSurveyResponseDto> ExecuteAsync(CancellationToken ct);
}
