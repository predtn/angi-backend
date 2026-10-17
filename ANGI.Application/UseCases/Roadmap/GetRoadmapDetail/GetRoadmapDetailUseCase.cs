using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.DTOs.Roadmap;

namespace ANGI.Application.UseCases.Roadmap.GetRoadmapDetail;

/// <summary>Implements RM-03 as an ownership-scoped read of the complete roadmap aggregate.</summary>
public sealed class GetRoadmapDetailUseCase : IGetRoadmapDetailUseCase
{
    private readonly IRoadmapRepository _roadmapRepository;
    private readonly ICurrentUserService _currentUserService;

    /// <summary>Initializes the roadmap persistence and current-user dependencies.</summary>
    public GetRoadmapDetailUseCase(
        IRoadmapRepository roadmapRepository,
        ICurrentUserService currentUserService)
    {
        _roadmapRepository = roadmapRepository;
        _currentUserService = currentUserService;
    }

    /// <summary>Loads an owned roadmap, current item feedback, and all calculated RM-03 sections.</summary>
    public async Task<RoadmapDetailDto> ExecuteAsync(long roadmapId, CancellationToken ct)
    {
        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xem roadmap.");
        var roadmap = await _roadmapRepository.GetOwnedDetailAsync(roadmapId, userId, ct)
            ?? throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");
        var itemIds = roadmap.Days.SelectMany(day => day.Items).Select(item => item.Id).ToArray();
        var feedback = await _roadmapRepository.GetFeedbackByItemIdsAsync(userId, itemIds, ct);

        return RoadmapMapper.Map(roadmap, feedback);
    }
}
