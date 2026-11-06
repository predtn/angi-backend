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
        // STEP-06: Prepare changes through submission.Items.ToDictionary, retained through Where(filtered).Select(filtered).ToHashSet and more.
        var changes = submission.Items.ToDictionary(item => item, item => RestaurantMenuMapper.MapItem(item).Change);
        var retained = submission.Items.Where(item => item.SourceDishId.HasValue)
            .Select(item => item.SourceDishId!.Value).ToHashSet();
        var removed = submission.Restaurant.Dishes
            .Where(dish => dish.Status != DishStatus.Removed && !retained.Contains(dish.Id)).ToArray();

        // STEP-07: For each dish in removed, set dish.Status.
        foreach (var dish in removed)
            dish.Status = DishStatus.Removed;
        // STEP-08: For each item in submission.Items, check whether item.SourceDishId is missing.
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

        // STEP-09: Call MenuReviewDecision.Record.
        MenuReviewDecision.Record(submission, reviewerId, request.ReviewNote, true, timeProvider, repository, auditLog);
        // STEP-10: Load repository.LoadReviewerAsync.
        await repository.LoadReviewerAsync(submission, ct);
        // STEP-11: Save the pending changes.
        await unitOfWork.SaveChangesAsync(ct);
        // STEP-12: Commit the transaction.
        await transaction.CommitAsync(ct);
        // STEP-13: Prepare source through RestaurantMenuMapper.MapSubmission.
        var source = RestaurantMenuMapper.MapSubmission(submission);
        // STEP-14: Return a new ApproveMenuSubmissionResponseDto.
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
