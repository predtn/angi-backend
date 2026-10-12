using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;

namespace ANGI.Application.UseCases.Restaurant.DeleteMenuSubmissionItem;

/// <summary>Implements OWN-16 by deleting one editable proposal row.</summary>
public sealed class DeleteMenuSubmissionItemUseCase : IDeleteMenuSubmissionItemUseCase
{
    private readonly IRestaurantMenuRepository _repository;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes owned-item lookup and persistence dependencies.</summary>
    public DeleteMenuSubmissionItemUseCase(
        IRestaurantMenuRepository repository,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
    }

    /// <summary>Ensures the submission is a draft and removes the selected proposal.</summary>
    public async Task ExecuteAsync(long submissionId, long itemId, CancellationToken ct)
    {
        var ownerId = _currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xóa món.");
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var submission = await _repository.LockOwnedSubmissionAsync(ownerId, submissionId, ct)
            ?? throw new NotFoundException("SUBMISSION_ITEM_NOT_FOUND", "Không tìm thấy món trong bản menu.");
        RestaurantMenuRules.EnsureEditable(submission);
        var item = await _repository.GetTrackedItemAsync(ownerId, submissionId, itemId, ct)
            ?? throw new NotFoundException("SUBMISSION_ITEM_NOT_FOUND", "Không tìm thấy món trong bản menu.");
        _repository.RemoveItem(item);
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
