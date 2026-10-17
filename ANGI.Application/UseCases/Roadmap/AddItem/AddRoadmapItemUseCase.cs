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
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để thêm món vào roadmap.");
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var target = await _roadmapRepository.LockAndGetOwnedDayAsync(roadmapId, dayId, userId, ct);
        if (!target.RoadmapExists)
        {
            throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");
        }

        var day = target.Day
            ?? throw new NotFoundException("DAY_NOT_FOUND", "Không tìm thấy ngày trong roadmap.");
        var dish = await _dishRepository.GetPublicDetailAsync(request.DishId, ct);
        if (dish is null || !dish.IsAvailable)
        {
            throw new BadRequestException("DISH_NOT_AVAILABLE", "Món không còn khả dụng để thêm vào roadmap.");
        }

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
        _roadmapRepository.AddItem(item);
        await _unitOfWork.SaveChangesAsync(ct);

        item.Dish = dish;
        item.RoadmapDay = day;
        var result = RoadmapMapper.MapItem(day, item);
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>Returns one greater than the largest order in the meal, or zero for an empty meal.</summary>
    private static short NextSortOrder(RoadmapDay day, MealSlot mealSlot)
    {
        var orders = day.Items.Where(item => item.MealSlot == mealSlot).Select(item => item.SortOrder).ToArray();
        if (orders.Length == 0)
        {
            return 0;
        }

        var maximum = orders.Max();
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
