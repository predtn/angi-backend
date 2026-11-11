using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.DTOs.Roadmap;

namespace ANGI.Application.UseCases.Roadmap.Generate;

/// <summary>Implements RM-11: reads one generation job of the caller's visible roadmap.</summary>
public sealed class GetRoadmapGenerationJobUseCase : IGetRoadmapGenerationJobUseCase
{
    private readonly IRoadmapGenerationRepository _generationRepository;
    private readonly ICurrentUserService _currentUserService;

    /// <summary>Initializes persistence and current-user dependencies.</summary>
    public GetRoadmapGenerationJobUseCase(
        IRoadmapGenerationRepository generationRepository,
        ICurrentUserService currentUserService)
    {
        _generationRepository = generationRepository;
        _currentUserService = currentUserService;
    }

    /// <summary>Maps the job; created item ids come from raw_output (no separate column).</summary>
    public async Task<RoadmapGenerationJobDto> ExecuteAsync(long roadmapId, long jobId, CancellationToken ct)
    {
        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xem tiến trình tạo roadmap.");
        var job = await _generationRepository.GetOwnedJobAsync(roadmapId, jobId, userId, ct)
            ?? throw new NotFoundException("JOB_NOT_FOUND", "Không tìm thấy tiến trình tạo roadmap.");

        return new RoadmapGenerationJobDto
        {
            JobId = job.Id,
            Scope = RoadmapGenerationValues.ToApi(job.Scope),
            Status = RoadmapGenerationValues.ToApi(job.Status),
            ErrorMessage = job.ErrorMessage,
            CreatedItemIds = RoadmapGenerationValues.ReadCreatedItemIds(job.RawOutput),
            CreatedAt = job.CreatedAt,
            FinishedAt = job.FinishedAt
        };
    }
}
