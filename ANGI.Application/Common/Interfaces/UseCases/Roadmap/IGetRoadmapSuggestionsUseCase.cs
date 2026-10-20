using ANGI.Application.DTOs.Roadmap;

namespace ANGI.Application.Common.Interfaces.UseCases.Roadmap;

/// <summary>Returns one ranked page of dishes that can be added to an owned roadmap.</summary>
public interface IGetRoadmapSuggestionsUseCase
{
    /// <summary>Executes RM-06 for the current traveler.</summary>
    Task<GetRoadmapSuggestionsResponseDto> ExecuteAsync(
        long roadmapId,
        GetRoadmapSuggestionsRequestDto request,
        CancellationToken ct);
}
