using ANGI.Application.DTOs.Roadmap;

namespace ANGI.Application.Common.Interfaces.UseCases.Roadmap;

/// <summary>Defines RM-08 replacement and repositioning of an owned roadmap item.</summary>
public interface IUpdateRoadmapItemUseCase
{
    /// <summary>Updates the requested item and returns its current representation or its replacement.</summary>
    Task<RoadmapItemDto> ExecuteAsync(
        long roadmapId,
        long itemId,
        UpdateRoadmapItemRequestDto request,
        CancellationToken ct);
}
