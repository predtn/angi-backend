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
        await _validator.ValidateAndThrowAsync(request, ct);

        var ownerId = _currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để nộp bản menu.");
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var submission = await _repository.LockOwnedSubmissionAsync(ownerId, submissionId, ct)
            ?? throw new NotFoundException("SUBMISSION_NOT_FOUND", "Không tìm thấy bản menu.");
        RestaurantMenuRules.EnsureEditable(submission);

        if (submission.Items.Count == 0)
        {
            throw new BadRequestException("MENU_EMPTY", "Bản menu không được để trống.");
        }

        var currentDishes = await _repository.GetDraftSourceDishesAsync(submission.RestaurantId, ct);
        if (!HasMenuChanges(submission.Items, currentDishes))
        {
            throw new BadRequestException(
                "MENU_UNCHANGED",
                "Bản menu chưa có thay đổi so với menu đang chạy.");
        }

        var previousStatus = submission.Status;
        var previousOwnerNote = submission.OwnerNote;
        var previousSubmittedAt = submission.SubmittedAt;
        try
        {
            submission.Status = MenuSubmissionStatus.Pending;
            submission.OwnerNote = NormalizeOptionalText(request.OwnerNote);
            submission.SubmittedAt = _timeProvider.GetUtcNow().UtcDateTime;

            await _unitOfWork.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            // The transaction is rolled back when disposed. Restore the aggregate as well so a
            // failed scoped operation cannot expose an in-memory state that was never committed.
            submission.Status = previousStatus;
            submission.OwnerNote = previousOwnerNote;
            submission.SubmittedAt = previousSubmittedAt;
            throw;
        }

        return RestaurantMenuMapper.MapSubmission(submission);
    }

    /// <summary>Compares every proposed item and removal with the restaurant's current menu.</summary>
    private static bool HasMenuChanges(
        ICollection<MenuSubmissionItem> items,
        IReadOnlyCollection<Dish> currentDishes)
    {
        if (items.Count != currentDishes.Count)
        {
            return true;
        }

        var currentDishesById = currentDishes.ToDictionary(dish => dish.Id);
        foreach (var item in items)
        {
            if (!item.SourceDishId.HasValue ||
                !currentDishesById.TryGetValue(item.SourceDishId.Value, out var sourceDish) ||
                !MatchesSourceDish(item, sourceDish))
            {
                return true;
            }
        }

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
