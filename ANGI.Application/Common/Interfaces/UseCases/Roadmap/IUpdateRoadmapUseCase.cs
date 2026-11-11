using ANGI.Application.DTOs.Roadmap;

namespace ANGI.Application.Common.Interfaces.UseCases.Roadmap;

/// <summary>Updates the supplied fields of a traveler-owned roadmap.</summary>
public interface IUpdateRoadmapUseCase
{
    /// <summary>Executes RM-04 for the authenticated traveler.</summary>
    Task<RoadmapDetailDto> ExecuteAsync(long roadmapId, UpdateRoadmapRequestDto request, CancellationToken ct);
}
