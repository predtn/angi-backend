using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Roadmap;

namespace ANGI.Application.Common.Interfaces.UseCases.Roadmap;

/// <summary>Defines the RM-02 operation that lists the current traveler's roadmaps.</summary>
public interface IGetMyRoadmapsUseCase
{
    /// <summary>Returns one validated page of non-deleted roadmaps owned by the current traveler.</summary>
    Task<Paged<RoadmapSummaryDto>> ExecuteAsync(GetMyRoadmapsRequestDto request, CancellationToken ct);
}
