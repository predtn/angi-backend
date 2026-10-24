using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;

namespace ANGI.Application.UseCases.Roadmap.DeleteItem;

/// <summary>Implements RM-09 as an ownership-scoped, atomic hard deletion.</summary>
public sealed class DeleteRoadmapItemUseCase : IDeleteRoadmapItemUseCase
{
    private readonly IRoadmapRepository _roadmapRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes the identity, persistence, and transaction dependencies.</summary>
    public DeleteRoadmapItemUseCase(
        IRoadmapRepository roadmapRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork)
    {
        _roadmapRepository = roadmapRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
    }

    /// <summary>Locks and deletes an owned item while preserving related feedback and reviews through their foreign keys.</summary>
    public async Task ExecuteAsync(long roadmapId, long itemId, CancellationToken ct)
    {
        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xóa món khỏi roadmap.");
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var item = await _roadmapRepository.LockAndGetOwnedItemForDeleteAsync(roadmapId, itemId, userId, ct)
            ?? throw new NotFoundException("ROADMAP_ITEM_NOT_FOUND", "Không tìm thấy món trong roadmap.");

        _roadmapRepository.RemoveItem(item);
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
