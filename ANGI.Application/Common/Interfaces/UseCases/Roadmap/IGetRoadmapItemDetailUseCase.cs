using ANGI.Application.DTOs.Roadmap;

namespace ANGI.Application.Common.Interfaces.UseCases.Roadmap;

/// <summary>Defines RM-12 retrieval of one owned roadmap item's complete dish and restaurant detail.</summary>
public interface IGetRoadmapItemDetailUseCase
{
    /// <summary>Returns the requested item when it belongs to a roadmap owned by the current traveler.</summary>
    Task<RoadmapItemDetailDto> ExecuteAsync(long roadmapId, long itemId, CancellationToken ct);
}
