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

namespace ANGI.Application.UseCases.Roadmap.AddItem;

/// <summary>Implements RM-07 as an ownership-scoped, atomic manual roadmap-item insertion.</summary>
public sealed class AddRoadmapItemUseCase : IAddRoadmapItemUseCase
{
    private readonly IValidator<AddRoadmapItemRequestDto> _validator;
    private readonly IRoadmapRepository _roadmapRepository;
    private readonly IDishRepository _dishRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes validation, persistence, current-user, and transaction dependencies.</summary>
    public AddRoadmapItemUseCase(
        IValidator<AddRoadmapItemRequestDto> validator,
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

    /// <summary>Validates availability, appends or positions the item, persists once, and returns RoadmapItem.</summary>
    public async Task<RoadmapItemDto> ExecuteAsync(
        long roadmapId,
        long dayId,
        AddRoadmapItemRequestDto request,
        CancellationToken ct)
    {
        // STEP-01: Validate the request.
        await _validator.ValidateAndThrowAsync(request, ct);

        // STEP-02: Prepare userId.
        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để thêm món vào roadmap.");
        // STEP-03: Open the database transaction.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        // STEP-04: Load target through roadmapRepository.LockAndGetOwnedDayAsync.
        var target = await _roadmapRepository.LockAndGetOwnedDayAsync(roadmapId, dayId, userId, ct);
        // STEP-05: Reject with ROADMAP_NOT_FOUND when target.RoadmapExists is false.
        if (!target.RoadmapExists)
        {
            throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");
        }

        // STEP-06: Prepare day, then load dish through dishRepository.GetPublicDetailAsync.
        var day = target.Day
            ?? throw new NotFoundException("DAY_NOT_FOUND", "Không tìm thấy ngày trong roadmap.");
        var dish = await _dishRepository.GetPublicDetailAsync(request.DishId, ct);
        // STEP-07: Reject with DISH_NOT_AVAILABLE when dish is missing, dish.IsAvailable is false or dish.Restaurant.OperatingStatus is not RestaurantOperatingStatus.Open.
        if (dish is null ||
            !dish.IsAvailable ||
            dish.Restaurant.OperatingStatus != RestaurantOperatingStatus.Open)
        {
            throw new BadRequestException("DISH_NOT_AVAILABLE", "Món không còn khả dụng để thêm vào roadmap.");
        }

        // STEP-08: Prepare mealSlot through ParseMealSlot and item as RoadmapItem.
        var mealSlot = ParseMealSlot(request.MealSlot);
        var item = new RoadmapItem
        {
            RoadmapDayId = day.Id,
            DishId = dish.Id,
            MealSlot = mealSlot,
            SortOrder = request.SortOrder ?? NextSortOrder(day, mealSlot),
            Source = RoadmapItemSource.Manual,
            RecoRequestId = request.RecoRequestId
        };
        // STEP-09: Queue item through roadmapRepository.AddItem.
        _roadmapRepository.AddItem(item);
        // STEP-10: Save the pending changes.
        await _unitOfWork.SaveChangesAsync(ct);

        // STEP-11: Update item: Dish and RoadmapDay.
        item.Dish = dish;
        item.RoadmapDay = day;
        // STEP-12: Prepare result through RoadmapMapper.MapItem.
        var result = RoadmapMapper.MapItem(day, item);
        // STEP-13: Commit the transaction.
        await transaction.CommitAsync(ct);
        // STEP-14: Return result.
        return result;
    }

    /// <summary>Returns one greater than the largest order in the meal, or zero for an empty meal.</summary>
    private static short NextSortOrder(RoadmapDay day, MealSlot mealSlot)
    {
        // STEP-01: Prepare orders through Where(filtered).Select(filtered).ToArray.
        var orders = day.Items.Where(item => item.MealSlot == mealSlot).Select(item => item.SortOrder).ToArray();
        // STEP-02: Return 0 early when orders.Length is 0.
        if (orders.Length == 0)
        {
            return 0;
        }

        // STEP-03: Prepare maximum through orders.Max.
        var maximum = orders.Max();
        // STEP-04: Return one of two results depending on maximum is short.MaxValue.
        // A tie at the smallint ceiling still appends deterministically because item ids are the final ordering key.
        return maximum == short.MaxValue ? short.MaxValue : (short)(maximum + 1);
    }

    /// <summary>Parses a validated public meal-slot value into its domain enum.</summary>
    private static MealSlot ParseMealSlot(string value) => value switch
    {
        "breakfast" => MealSlot.Breakfast,
        "lunch" => MealSlot.Lunch,
        "dinner" => MealSlot.Dinner,
        "snack" => MealSlot.Snack,
        _ => MealSlot.LateNight
    };
}
