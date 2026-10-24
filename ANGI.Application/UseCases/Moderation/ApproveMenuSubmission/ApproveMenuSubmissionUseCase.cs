using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;

namespace ANGI.Application.UseCases.Moderation.ApproveMenuSubmission;

/// <summary>Implements MOD-M3 by atomically replacing a pending menu while preserving existing dish identities.</summary>
public sealed class ApproveMenuSubmissionUseCase(
    IMenuReviewRepository repository, ICurrentUserService currentUser,
    IUnitOfWork unitOfWork, IAuditLogService auditLog, TimeProvider timeProvider,
    IValidator<ApproveMenuSubmissionRequestDto> validator) : IApproveMenuSubmissionUseCase
{
    /// <summary>Serializes review, applies menu changes, and commits dishes, notification and audit together.</summary>
    public async Task<ApproveMenuSubmissionResponseDto> ExecuteAsync(
        long submissionId, ApproveMenuSubmissionRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var reviewerId = await MenuPermissionGuard.RequireAsync(currentUser, repository, ct);
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var submission = await repository.LockAsync(submissionId, ct)
            ?? throw new NotFoundException("SUBMISSION_NOT_FOUND", "KhÃ´ng tÃ¬m tháº¥y báº£n menu.");
        MenuReviewDecision.RequirePending(submission);
        var changes = submission.Items.ToDictionary(item => item, item => RestaurantMenuMapper.MapItem(item).Change);
        var retained = submission.Items.Where(item => item.SourceDishId.HasValue)
            .Select(item => item.SourceDishId!.Value).ToHashSet();
        var removed = submission.Restaurant.Dishes
            .Where(dish => dish.Status != DishStatus.Removed && !retained.Contains(dish.Id)).ToArray();

        foreach (var dish in removed)
            dish.Status = DishStatus.Removed;
        foreach (var item in submission.Items)
        {
            if (item.SourceDishId is null)
            {
                var dish = new Dish { RestaurantId = submission.RestaurantId };
                CopyItem(item, dish);
                foreach (var tagId in item.TagIds)
                    dish.DishTags.Add(new DishTag { TagId = tagId });
                repository.AddDish(item, dish);
            }
            else
            {
                var dish = item.SourceDish
                    ?? throw new InvalidOperationException("The source dish graph was not loaded.");
                if (dish.RestaurantId != submission.RestaurantId)
                    throw new InvalidOperationException("A menu item references a different restaurant.");
                if (changes[item] == "edited")
                {
                    CopyItem(item, dish);
                    repository.ReplaceTags(dish, item.TagIds);
                }
                item.ResultDishId = dish.Id;
            }
        }

        MenuReviewDecision.Record(submission, reviewerId, request.ReviewNote, true, timeProvider, repository, auditLog);
        await repository.LoadReviewerAsync(submission, ct);
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var source = RestaurantMenuMapper.MapSubmission(submission);
        return new ApproveMenuSubmissionResponseDto
        {
            Id = source.Id, RestaurantId = source.RestaurantId, Status = source.Status,
            OwnerNote = source.OwnerNote, SubmittedAt = source.SubmittedAt,
            ReviewedBy = source.ReviewedBy, ReviewedAt = source.ReviewedAt,
            ReviewNote = source.ReviewNote, CreatedAt = source.CreatedAt,
            CreatedDishIds = changes.Where(pair => pair.Value == "new").Select(pair => pair.Key.ResultDishId!.Value).ToArray(),
            UpdatedDishIds = changes.Where(pair => pair.Value == "edited").Select(pair => pair.Key.SourceDishId!.Value).ToArray(),
            RemovedDishIds = removed.Select(dish => dish.Id).ToArray()
        };
    }

    /// <summary>Copies proposal fields while preserving dish identity, moderation state, availability and engagement.</summary>
    private static void CopyItem(MenuSubmissionItem item, Dish dish)
    {
        dish.Name = item.Name;
        dish.Description = item.Description;
        dish.DishForm = item.DishForm;
        dish.Price = item.Price;
        dish.CoverMediaId = item.CoverMediaId;
        dish.SortOrder = item.SortOrder;
    }
}
