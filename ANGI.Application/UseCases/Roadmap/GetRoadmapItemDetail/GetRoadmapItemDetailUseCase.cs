using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Discovery;
using ANGI.Application.UseCases.Restaurant;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Roadmap.GetRoadmapItemDetail;

/// <summary>Implements RM-12 as an ownership-scoped read of one roadmap item and its related detail.</summary>
public sealed class GetRoadmapItemDetailUseCase : IGetRoadmapItemDetailUseCase
{
    private readonly IRoadmapRepository _roadmapRepository;
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes persistence, current-user, and deterministic time dependencies.</summary>
    public GetRoadmapItemDetailUseCase(
        IRoadmapRepository roadmapRepository,
        IRestaurantRepository restaurantRepository,
        ICurrentUserService currentUserService,
        TimeProvider timeProvider)
    {
        _roadmapRepository = roadmapRepository;
        _restaurantRepository = restaurantRepository;
        _currentUserService = currentUserService;
        _timeProvider = timeProvider;
    }

    /// <summary>Loads an owned item, its feedback and review, then maps the complete RM-12 response.</summary>
    public async Task<RoadmapItemDetailDto> ExecuteAsync(long roadmapId, long itemId, CancellationToken ct)
    {
        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xem chi tiết món trong roadmap.");
        var item = await _roadmapRepository.GetOwnedItemDetailAsync(roadmapId, itemId, userId, ct)
            ?? throw new NotFoundException("ROADMAP_ITEM_NOT_FOUND", "Không tìm thấy món trong roadmap.");
        var feedback = await _roadmapRepository.GetFeedbackByItemIdsAsync(userId, [item.Id], ct);
        var review = await _restaurantRepository.GetTravelerReviewForRestaurantAsync(
            item.Dish.RestaurantId,
            userId,
            ct);
        var reviewDto = review is null ? null : RestaurantDetailMapper.MapReview(review);
        var baseItem = RoadmapMapper.MapItem(item.RoadmapDay, item, feedback);

        return new RoadmapItemDetailDto
        {
            Id = baseItem.Id,
            DayId = baseItem.DayId,
            DayNumber = baseItem.DayNumber,
            MealSlot = baseItem.MealSlot,
            SortOrder = baseItem.SortOrder,
            Source = baseItem.Source,
            RecoRequestId = baseItem.RecoRequestId,
            MyFeedback = baseItem.MyFeedback,
            CreatedAt = baseItem.CreatedAt,
            Dish = DishDetailMapper.Map(item.Dish, _timeProvider),
            Restaurant = RestaurantDetailMapper.MapDetail(
                item.Dish.Restaurant,
                _timeProvider,
                myReview: reviewDto),
            CanRate = review is null,
            MyReview = reviewDto
        };
    }
}
