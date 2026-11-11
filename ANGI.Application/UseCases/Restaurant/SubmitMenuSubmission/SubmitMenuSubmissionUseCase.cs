using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Restaurant.SubmitMenuSubmission;

/// <summary>Implements OWN-17 by submitting a changed menu draft for moderator review.</summary>
public sealed class SubmitMenuSubmissionUseCase : ISubmitMenuSubmissionUseCase
{
    private readonly IValidator<SubmitMenuSubmissionRequestDto> _validator;
    private readonly IRestaurantMenuRepository _repository;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes validation, ownership, persistence, and clock dependencies.</summary>
    public SubmitMenuSubmissionUseCase(
        IValidator<SubmitMenuSubmissionRequestDto> validator,
        IRestaurantMenuRepository repository,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _repository = repository;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <summary>Validates a draft, confirms that it changes the live menu, and marks it pending.</summary>
    public async Task<MenuSubmissionDto> ExecuteAsync(
        long submissionId,
        SubmitMenuSubmissionRequestDto request,
        CancellationToken ct)
    {
        // STEP-01: Validate the request.
        await _validator.ValidateAndThrowAsync(request, ct);

        // STEP-02: Prepare ownerId.
        var ownerId = _currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để nộp bản menu.");
        // STEP-03: Open the database transaction.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        // STEP-04: Prepare submission from repository.LockOwnedSubmissionAsync.
        var submission = await _repository.LockOwnedSubmissionAsync(ownerId, submissionId, ct)
            ?? throw new NotFoundException("SUBMISSION_NOT_FOUND", "Không tìm thấy bản menu.");
        // STEP-05: Check the editable rule through RestaurantMenuRules.EnsureEditable.
        RestaurantMenuRules.EnsureEditable(submission);

        // STEP-06: Reject with MENU_EMPTY when submission.Items.Count is 0.
        if (submission.Items.Count == 0)
        {
            throw new BadRequestException("MENU_EMPTY", "Bản menu không được để trống.");
        }

        // STEP-07: Load currentDishes through repository.GetDraftSourceDishesAsync.
        var currentDishes = await _repository.GetDraftSourceDishesAsync(submission.RestaurantId, ct);
        // STEP-08: Reject with MENU_UNCHANGED when HasMenuChanges(submission.Items, currentDishes) does not hold.
        if (!HasMenuChanges(submission.Items, currentDishes))
        {
            throw new BadRequestException(
                "MENU_UNCHANGED",
                "Bản menu chưa có thay đổi so với menu đang chạy.");
        }

        // STEP-09: Prepare previousStatus, previousOwnerNote and previousSubmittedAt.
        var previousStatus = submission.Status;
        var previousOwnerNote = submission.OwnerNote;
        var previousSubmittedAt = submission.SubmittedAt;
        try
        {
            // STEP-10: Update submission: Status, OwnerNote and SubmittedAt.
            submission.Status = MenuSubmissionStatus.Pending;
            submission.OwnerNote = NormalizeOptionalText(request.OwnerNote);
            submission.SubmittedAt = _timeProvider.GetUtcNow().UtcDateTime;

            // STEP-11: Save the pending changes.
            await _unitOfWork.SaveChangesAsync(ct);
            // STEP-12: Commit the transaction.
            await transaction.CommitAsync(ct);
        }
        catch
        {
            // STEP-13: On any exception, set submission.Status, set submission.OwnerNote and 2 more action(s).
            // The transaction is rolled back when disposed. Restore the aggregate as well so a
            // failed scoped operation cannot expose an in-memory state that was never committed.
            submission.Status = previousStatus;
            submission.OwnerNote = previousOwnerNote;
            submission.SubmittedAt = previousSubmittedAt;
            throw;
        }

        // STEP-14: Return the result of RestaurantMenuMapper.MapSubmission.
        return RestaurantMenuMapper.MapSubmission(submission);
    }

    /// <summary>Compares every proposed item and removal with the restaurant's current menu.</summary>
    private static bool HasMenuChanges(
        ICollection<MenuSubmissionItem> items,
        IReadOnlyCollection<Dish> currentDishes)
    {
        // STEP-01: Return true early when items.Count is not currentDishes.Count.
        if (items.Count != currentDishes.Count)
        {
            return true;
        }

        // STEP-02: Prepare currentDishesById through currentDishes.ToDictionary.
        var currentDishesById = currentDishes.ToDictionary(dish => dish.Id);
        // STEP-03: For each item in items, return true early when item.SourceDishId has no value and more.
        foreach (var item in items)
        {
            if (!item.SourceDishId.HasValue ||
                !currentDishesById.TryGetValue(item.SourceDishId.Value, out var sourceDish) ||
                !MatchesSourceDish(item, sourceDish))
            {
                return true;
            }
        }

        // STEP-04: Return false.
        return false;
    }

    /// <summary>Checks whether one proposed item still matches all editable source-dish fields.</summary>
    private static bool MatchesSourceDish(MenuSubmissionItem item, Dish sourceDish)
    {
        return item.Name == sourceDish.Name &&
               item.Description == sourceDish.Description &&
               item.DishForm == sourceDish.DishForm &&
               item.Price == sourceDish.Price &&
               item.CoverMediaId == sourceDish.CoverMediaId &&
               item.SortOrder == sourceDish.SortOrder &&
               item.TagIds.OrderBy(id => id)
                   .SequenceEqual(sourceDish.DishTags.Select(tag => tag.TagId).OrderBy(id => id));
    }

    /// <summary>Trims an optional note and stores whitespace-only input as null.</summary>
    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
