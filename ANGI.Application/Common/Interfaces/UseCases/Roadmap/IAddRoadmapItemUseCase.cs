using ANGI.Application.DTOs.Roadmap;

namespace ANGI.Application.Common.Interfaces.UseCases.Roadmap;

/// <summary>Adds one manually selected dish to an owned roadmap day.</summary>
public interface IAddRoadmapItemUseCase
{
    /// <summary>Executes RM-07 for the current traveler.</summary>
    Task<RoadmapItemDto> ExecuteAsync(
        long roadmapId,
        long dayId,
        AddRoadmapItemRequestDto request,
        CancellationToken ct);
}
