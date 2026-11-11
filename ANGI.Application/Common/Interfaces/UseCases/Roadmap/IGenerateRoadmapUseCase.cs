using ANGI.Application.DTOs.Roadmap;

namespace ANGI.Application.Common.Interfaces.UseCases.Roadmap;

/// <summary>Defines RM-10: queue an AI generation job for an owned roadmap.</summary>
public interface IGenerateRoadmapUseCase
{
    /// <summary>
    /// Queues the job; raises 404 ROADMAP_NOT_FOUND, 404 DAY_NOT_FOUND for a day outside the roadmap and 409
    /// GENERATION_IN_PROGRESS while another job of the roadmap is queued or running.
    /// </summary>
    Task<GenerateRoadmapResponseDto> ExecuteAsync(long roadmapId, GenerateRoadmapRequestDto request, CancellationToken ct);
}
