using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Roadmap.UpdateItem;

/// <summary>Implements RM-08 as an atomic item replacement or in-place schedule update.</summary>
public sealed class UpdateRoadmapItemUseCase : IUpdateRoadmapItemUseCase
{
    private readonly IValidator<UpdateRoadmapItemRequestDto> _validator;
    private readonly IRoadmapRepository _roadmapRepository;
    private readonly IDishRepository _dishRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes validation, persistence, identity, and transaction dependencies.</summary>
    public UpdateRoadmapItemUseCase(
        IValidator<UpdateRoadmapItemRequestDto> validator,
        IRoadmapRepository roadmapRepository,
        IDishRepository dishRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork)
    {
        _validator = validator;
        _roadmapRepository = roadmapRepository;
        _dishRepository = dishRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
    }

    /// <summary>Validates RM-08, locks ownership, applies the patch, saves once, and returns RoadmapItem.</summary>
    public async Task<RoadmapItemDto> ExecuteAsync(
        long roadmapId,
        long itemId,
        UpdateRoadmapItemRequestDto request,
        CancellationToken ct)
    {
        // STEP-01: Validate the request.
        await _validator.ValidateAndThrowAsync(request, ct);

        // STEP-02: Prepare userId.
        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để sửa món trong roadmap.");
        // STEP-03: Open the database transaction.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        // STEP-04: Prepare currentItem, then call ResolveDestinationDayAsync and keep destinationDay.
        var currentItem = await _roadmapRepository.LockAndGetOwnedItemAsync(roadmapId, itemId, userId, ct)
            ?? throw ItemNotFound();
        var destinationDay = await ResolveDestinationDayAsync(
            roadmapId,
            userId,
            currentItem.RoadmapDay,
            request.DayId,
            ct);
        // STEP-05: Prepare mealSlot from ParseMealSlot, sortOrder, resultItem, replacementDish and 1 more.
        var mealSlot = request.MealSlot is null ? currentItem.MealSlot : ParseMealSlot(request.MealSlot);
        var sortOrder = request.SortOrder ?? currentItem.SortOrder;

        RoadmapItem resultItem;
        Dish? replacementDish = null;
        IReadOnlyDictionary<long, FeedbackValue>? feedback = null;
        // STEP-06: When request.DishId has a value, run the branch below.
        if (request.DishId.HasValue)
        {
            // STEP-07: Load replacementDish through dishRepository.GetPublicDetailAsync.
            replacementDish = await _dishRepository.GetPublicDetailAsync(request.DishId.Value, ct);
            // STEP-08: Reject with DISH_NOT_AVAILABLE when replacementDish is missing, replacementDish.IsAvailable is false or Restaurant.OperatingStatus is not RestaurantOperatingStatus.Open.
            if (replacementDish is null ||
                !replacementDish.IsAvailable ||
                replacementDish.Restaurant.OperatingStatus != RestaurantOperatingStatus.Open)
            {
                throw new BadRequestException(
                    "DISH_NOT_AVAILABLE",
                    "Món không còn khả dụng để thay thế trong roadmap.");
            }

            // STEP-09: Set resultItem.
            resultItem = new RoadmapItem
            {
                RoadmapDayId = destinationDay.Id,
                RoadmapDay = destinationDay,
                DishId = replacementDish.Id,
                MealSlot = mealSlot,
                SortOrder = sortOrder,
                Source = RoadmapItemSource.Manual,
                RecoRequestId = request.RecoRequestId
            };
            // STEP-10: Mark currentItem for removal through roadmapRepository.RemoveItem and queue resultItem through roadmapRepository.AddItem.
            _roadmapRepository.RemoveItem(currentItem);
            _roadmapRepository.AddItem(resultItem);
        }
        else
        {
            // STEP-11: Otherwise, set feedback, set currentItem.RoadmapDayId and 4 more action(s).
            feedback = await _roadmapRepository.GetFeedbackByItemIdsAsync(userId, [currentItem.Id], ct);
            // STEP-12: Set currentItem.RoadmapDayId, currentItem.RoadmapDay, currentItem.MealSlot, currentItem.SortOrder and 1 more.
            currentItem.RoadmapDayId = destinationDay.Id;
            currentItem.RoadmapDay = destinationDay;
            currentItem.MealSlot = mealSlot;
            currentItem.SortOrder = sortOrder;
            resultItem = currentItem;
        }

        // STEP-13: Save the pending changes.
        await _unitOfWork.SaveChangesAsync(ct);
        // STEP-14: When replacementDish exists, set resultItem.Dish.
        if (replacementDish is not null)
        {
            // Persist only DishId: attaching the no-tracking response graph can conflict with the old tracked dish graph.
            resultItem.Dish = replacementDish;
        }

        // STEP-15: Prepare result through RoadmapMapper.MapItem.
        var result = RoadmapMapper.MapItem(destinationDay, resultItem, feedback);
        // STEP-16: Commit the transaction.
        await transaction.CommitAsync(ct);
        // STEP-17: Return result.
        return result;
    }

    /// <summary>Returns the current day or resolves a destination constrained to the same owned roadmap.</summary>
    private async Task<RoadmapDay> ResolveDestinationDayAsync(
        long roadmapId,
        int userId,
        RoadmapDay currentDay,
        long? requestedDayId,
        CancellationToken ct)
    {
        if (!requestedDayId.HasValue || requestedDayId.Value == currentDay.Id)
        {
            return currentDay;
        }

        return await _roadmapRepository.GetOwnedDayAsync(roadmapId, requestedDayId.Value, userId, ct)
            ?? throw ItemNotFound();
    }

    /// <summary>Creates the ownership-safe missing-resource error documented for RM-08.</summary>
    private static NotFoundException ItemNotFound() => new(
        "ROADMAP_ITEM_NOT_FOUND",
        "Không tìm thấy món trong roadmap.");

    /// <summary>Parses a validator-approved public meal-slot value into its domain enum.</summary>
    private static MealSlot ParseMealSlot(string value) => value switch
    {
        "breakfast" => MealSlot.Breakfast,
        "lunch" => MealSlot.Lunch,
        "dinner" => MealSlot.Dinner,
        "snack" => MealSlot.Snack,
        _ => MealSlot.LateNight
    };
}
