using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Restaurant.CancelMenuSubmission;

/// <summary>Implements OWN-18 by cancelling an open menu workflow.</summary>
public sealed class CancelMenuSubmissionUseCase : ICancelMenuSubmissionUseCase
{
    private readonly IRestaurantMenuRepository _repository;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes owned submission lookup and persistence dependencies.</summary>
    public CancelMenuSubmissionUseCase(
        IRestaurantMenuRepository repository,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
    }

    /// <summary>Moves a draft or pending submission to the cancelled state.</summary>
    public async Task<MenuSubmissionDto> ExecuteAsync(long submissionId, CancellationToken ct)
    {
        // STEP-01: Prepare ownerId.
        var ownerId = _currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để hủy bản menu.");
        // STEP-02: Open the database transaction.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        // STEP-03: Prepare submission from repository.LockOwnedSubmissionAsync.
        var submission = await _repository.LockOwnedSubmissionAsync(ownerId, submissionId, ct)
            ?? throw new NotFoundException("SUBMISSION_NOT_FOUND", "Không tìm thấy bản menu.");
        // STEP-04: Reject with SUBMISSION_NOT_CANCELLABLE when submission.Status is the computed value.
        if (submission.Status is not (MenuSubmissionStatus.Draft or MenuSubmissionStatus.Pending))
        {
            throw new ConflictException(
                "SUBMISSION_NOT_CANCELLABLE",
                "Bản menu không còn có thể hủy.");
        }

        // STEP-05: Update submission: Status.
        submission.Status = MenuSubmissionStatus.Cancelled;
        // STEP-06: Save the pending changes.
        await _unitOfWork.SaveChangesAsync(ct);
        // STEP-07: Commit the transaction.
        await transaction.CommitAsync(ct);
        // STEP-08: Return the result of RestaurantMenuMapper.MapSubmission.
        return RestaurantMenuMapper.MapSubmission(submission);
    }
}
