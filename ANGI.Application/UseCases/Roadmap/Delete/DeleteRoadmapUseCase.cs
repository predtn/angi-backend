using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;

namespace ANGI.Application.UseCases.Roadmap.Delete;

/// <summary>Implements RM-05 as an ownership-scoped, atomic soft deletion.</summary>
public sealed class DeleteRoadmapUseCase : IDeleteRoadmapUseCase
{
    private readonly IRoadmapRepository _roadmapRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes identity, persistence, transaction, and clock dependencies.</summary>
    public DeleteRoadmapUseCase(
        IRoadmapRepository roadmapRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _roadmapRepository = roadmapRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <summary>Locks an owned visible roadmap, records its UTC deletion time, and commits the change.</summary>
    public async Task ExecuteAsync(long roadmapId, CancellationToken ct)
    {
        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xóa roadmap.");
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var roadmap = await _roadmapRepository.LockAndGetTrackedForDeleteAsync(roadmapId, userId, ct)
            ?? throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");

        roadmap.DeletedAt = _timeProvider.GetUtcNow().UtcDateTime;
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
