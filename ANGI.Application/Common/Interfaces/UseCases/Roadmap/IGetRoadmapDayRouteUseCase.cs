using ANGI.Application.DTOs.Roadmap;

namespace ANGI.Application.Common.Interfaces.UseCases.Roadmap;

/// <summary>Calculates the RM-14 travel route for one day of a traveler-owned roadmap.</summary>
public interface IGetRoadmapDayRouteUseCase
{
    /// <summary>Returns ordered route legs using GPS, the pinned origin, or no origin.</summary>
    Task<GetRoadmapDayRouteResponseDto> ExecuteAsync(
        long roadmapId,
        long dayId,
        GetRoadmapDayRouteRequestDto request,
        CancellationToken ct);
}
