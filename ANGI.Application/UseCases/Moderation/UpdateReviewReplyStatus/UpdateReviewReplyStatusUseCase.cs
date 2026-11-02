using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.UpdateReviewReplyStatus;

/// <summary>Implements MOD-F3 with atomic review reply status changes and audit logging.</summary>
public sealed class UpdateReviewReplyStatusUseCase(
    IValidator<UpdateReviewReplyStatusRequestDto> validator,
    ModerationAccess access,
    IReviewModerationRepository repository,
    IAuditLogService audit,
    IUnitOfWork unitOfWork) : IUpdateReviewReplyStatusUseCase
{
    public async Task<UpdateReviewReplyStatusResponseDto> ExecuteAsync(
        long reviewId,
        UpdateReviewReplyStatusRequestDto request,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        await access.RequireAsync(ct, "REVIEW_MODERATE");

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var review = await repository.LockReviewWithReplyAsync(reviewId, ct)
            ?? throw new NotFoundException("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá.");

        if (review.Reply is null || string.IsNullOrWhiteSpace(review.Reply.Content))
        {
            throw new BadRequestException("VALIDATION_FAILED", "Bài đánh giá chưa có phản hồi từ chủ nhà hàng.");
        }

        var targetStatus = request.Status switch
        {
            "visible" => ContentStatus.Visible,
            "hidden" => ContentStatus.Hidden,
            "removed" => ContentStatus.Removed,
            _ => throw new BadRequestException("VALIDATION_FAILED", "Trạng thái phản hồi không hợp lệ.")
        };

        if (review.Reply.Status != targetStatus)
        {
            var oldStatus = review.Reply.Status;
            review.Reply.Status = targetStatus;

            var action = targetStatus switch
            {
                ContentStatus.Visible => AuditActions.ReviewReplyRestored,
                ContentStatus.Hidden => AuditActions.ReviewReplyHidden,
                _ => AuditActions.ReviewReplyRemoved
            };

            audit.Add(new AuditLogEntry
            {
                Action = action,
                EntityType = AuditEntityTypes.ReviewReply,
                EntityId = review.Id,
                SubjectUserId = review.Reply.ReplierId,
                OldValues = new { Status = oldStatus.ToString().ToLowerInvariant() },
                NewValues = new { Status = targetStatus.ToString().ToLowerInvariant(), Reason = request.Reason }
            });

            await unitOfWork.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        return new UpdateReviewReplyStatusResponseDto
        {
            ReviewId = review.Id,
            ReplyStatus = request.Status
        };
    }
}
