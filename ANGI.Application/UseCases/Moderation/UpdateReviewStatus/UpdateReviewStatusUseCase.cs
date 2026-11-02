using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.UpdateReviewStatus;

/// <summary>Implements MOD-F2 with atomic review status changes, rating recalculation and audit logging.</summary>
public sealed class UpdateReviewStatusUseCase(
    IValidator<UpdateReviewStatusRequestDto> validator,
    ModerationAccess access,
    IReviewModerationRepository repository,
    IAuditLogService audit,
    IUnitOfWork unitOfWork) : IUpdateReviewStatusUseCase
{
    public async Task<UpdateReviewStatusResponseDto> ExecuteAsync(
        long reviewId,
        UpdateReviewStatusRequestDto request,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        await access.RequireAsync(ct, "REVIEW_MODERATE");

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var review = await repository.LockReviewWithRestaurantAsync(reviewId, ct)
            ?? throw new NotFoundException("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá.");

        var targetStatus = request.Status switch
        {
            "visible" => ContentStatus.Visible,
            "hidden" => ContentStatus.Hidden,
            "removed" => ContentStatus.Removed,
            _ => throw new BadRequestException("VALIDATION_FAILED", "Trạng thái đánh giá không hợp lệ.")
        };

        if (review.Status != targetStatus)
        {
            var oldStatus = review.Status;
            review.Status = targetStatus;

            if (oldStatus == ContentStatus.Visible || targetStatus == ContentStatus.Visible)
            {
                var (visibleCount, ratingSum) = await repository.GetVisibleRatingStatsAsync(review.RestaurantId, ct);
                int newCount;
                int newSum;

                if (oldStatus == ContentStatus.Visible && targetStatus != ContentStatus.Visible)
                {
                    newCount = Math.Max(0, visibleCount - 1);
                    newSum = Math.Max(0, ratingSum - review.Rating);
                }
                else
                {
                    newCount = visibleCount + 1;
                    newSum = ratingSum + review.Rating;
                }

                if (newCount == 0)
                {
                    review.Restaurant.RatingCount = 0;
                    review.Restaurant.RatingAvg = 0m;
                }
                else
                {
                    review.Restaurant.RatingCount = newCount;
                    review.Restaurant.RatingAvg = Math.Round((decimal)newSum / newCount, 2, MidpointRounding.AwayFromZero);
                }
            }

            var action = targetStatus switch
            {
                ContentStatus.Visible => AuditActions.ReviewRestored,
                ContentStatus.Hidden => AuditActions.ReviewHidden,
                _ => AuditActions.ReviewRemoved
            };

            audit.Add(new AuditLogEntry
            {
                Action = action,
                EntityType = AuditEntityTypes.Review,
                EntityId = review.Id,
                SubjectUserId = review.UserId,
                OldValues = new { Status = oldStatus.ToString().ToLowerInvariant() },
                NewValues = new { Status = targetStatus.ToString().ToLowerInvariant(), Reason = request.Reason }
            });

            await unitOfWork.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        return new UpdateReviewStatusResponseDto
        {
            ReviewId = review.Id,
            Status = request.Status
        };
    }
}
