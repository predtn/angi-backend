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
        await validator.ValidateAndThrowAsync(request, ct);
        var reviewerId = await MenuPermissionGuard.RequireAsync(currentUser, repository, ct);
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var submission = await repository.LockAsync(submissionId, ct)
            ?? throw new NotFoundException("SUBMISSION_NOT_FOUND", "Không tìm thấy bản menu.");
        MenuReviewDecision.RequirePending(submission);
        MenuReviewDecision.Record(submission, reviewerId, request.ReviewNote, false, timeProvider, repository, auditLog);
        await repository.LoadReviewerAsync(submission, ct);
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return RestaurantMenuMapper.MapSubmission(submission);
    }
}
