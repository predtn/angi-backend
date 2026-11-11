using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant;
using FluentValidation;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;

namespace ANGI.Application.UseCases.Moderation.RejectMenuSubmission;

/// <summary>Implements MOD-M4 without changing the restaurant's live menu.</summary>
public sealed class RejectMenuSubmissionUseCase(
    IMenuReviewRepository repository, ICurrentUserService currentUser,
    IUnitOfWork unitOfWork, IAuditLogService auditLog, TimeProvider timeProvider,
    IValidator<RejectMenuSubmissionRequestDto> validator) : IRejectMenuSubmissionUseCase
{
    /// <summary>Locks a pending submission and atomically records its rejection, reason and owner notification.</summary>
    public async Task<MenuSubmissionDto> ExecuteAsync(
        long submissionId, RejectMenuSubmissionRequestDto request, CancellationToken ct)
    {
        // STEP-01: Validate the request.
        await validator.ValidateAndThrowAsync(request, ct);
        // STEP-02: Check the caller's permission through MenuPermissionGuard.RequireAsync.
        var reviewerId = await MenuPermissionGuard.RequireAsync(currentUser, repository, ct);
        // STEP-03: Open the database transaction.
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        // STEP-04: Prepare submission from repository.LockAsync.
        var submission = await repository.LockAsync(submissionId, ct)
            ?? throw new NotFoundException("SUBMISSION_NOT_FOUND", "Không tìm thấy bản menu.");
        // STEP-05: Check the pending rule through MenuReviewDecision.RequirePending.
        MenuReviewDecision.RequirePending(submission);
        // STEP-06: Call MenuReviewDecision.Record.
        MenuReviewDecision.Record(submission, reviewerId, request.ReviewNote, false, timeProvider, repository, auditLog);
        // STEP-07: Load repository.LoadReviewerAsync.
        await repository.LoadReviewerAsync(submission, ct);
        // STEP-08: Save the pending changes.
        await unitOfWork.SaveChangesAsync(ct);
        // STEP-09: Commit the transaction.
        await transaction.CommitAsync(ct);
        // STEP-10: Return the result of RestaurantMenuMapper.MapSubmission.
        return RestaurantMenuMapper.MapSubmission(submission);
    }
}
