using ANGI.Application.DTOs.Roadmap;

namespace ANGI.Application.Common.Interfaces.UseCases.Roadmap;

/// <summary>Defines RM-11: the state of one generation job of an owned roadmap.</summary>
public interface IGetRoadmapGenerationJobUseCase
{
    /// <summary>Raises 404 JOB_NOT_FOUND when the job does not belong to the caller's visible roadmap.</summary>
    Task<RoadmapGenerationJobDto> ExecuteAsync(long roadmapId, long jobId, CancellationToken ct);
}
