using ANGI.Application.DTOs.Roadmap;

namespace ANGI.Application.Common.Interfaces.UseCases.Roadmap;

/// <summary>Loads the complete RM-03 detail of a traveler-owned roadmap.</summary>
public interface IGetRoadmapDetailUseCase
{
    /// <summary>Returns Overview, Itinerary, and Budget for the requested owned roadmap.</summary>
    Task<RoadmapDetailDto> ExecuteAsync(long roadmapId, CancellationToken ct);
}
