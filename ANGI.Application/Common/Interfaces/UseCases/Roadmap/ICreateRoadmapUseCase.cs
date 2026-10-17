using ANGI.Application.DTOs.Roadmap;

namespace ANGI.Application.Common.Interfaces.UseCases.Roadmap;

/// <summary>Creates a personal roadmap and its initially empty days.</summary>
public interface ICreateRoadmapUseCase
{
    /// <summary>Executes RM-01 for the authenticated traveler.</summary>
    Task<RoadmapDetailDto> ExecuteAsync(CreateRoadmapRequestDto request, CancellationToken ct);
}
