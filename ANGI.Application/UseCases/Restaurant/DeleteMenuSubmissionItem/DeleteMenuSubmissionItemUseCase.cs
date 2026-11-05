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
        // STEP-01: Prepare ownerId.
        var ownerId = _currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xóa món.");
        // STEP-02: Open the database transaction.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        // STEP-03: Prepare submission from repository.LockOwnedSubmissionAsync.
        var submission = await _repository.LockOwnedSubmissionAsync(ownerId, submissionId, ct)
            ?? throw new NotFoundException("SUBMISSION_ITEM_NOT_FOUND", "Không tìm thấy món trong bản menu.");
        // STEP-04: Check the editable rule through RestaurantMenuRules.EnsureEditable.
        RestaurantMenuRules.EnsureEditable(submission);
        // STEP-05: Prepare item from repository.GetTrackedItemAsync.
        var item = await _repository.GetTrackedItemAsync(ownerId, submissionId, itemId, ct)
            ?? throw new NotFoundException("SUBMISSION_ITEM_NOT_FOUND", "Không tìm thấy món trong bản menu.");
        // STEP-06: Mark item for removal through repository.RemoveItem.
        _repository.RemoveItem(item);
        // STEP-07: Save the pending changes.
        await _unitOfWork.SaveChangesAsync(ct);
        // STEP-08: Commit the transaction.
        await transaction.CommitAsync(ct);
    }
}
