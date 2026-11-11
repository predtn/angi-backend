using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Restaurant.GetRestaurantDetail;

/// <summary>Implements REST-02 by returning detailed restaurant information for public callers.</summary>
public sealed class GetRestaurantDetailUseCase : IGetRestaurantDetailUseCase
{
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly IValidator<GetRestaurantDetailRequestDto> _validator;
    private readonly ICurrentUserService _currentUserService;
    private readonly TimeProvider _timeProvider;

    public GetRestaurantDetailUseCase(
        IRestaurantRepository restaurantRepository,
        IValidator<GetRestaurantDetailRequestDto> validator,
        ICurrentUserService currentUserService,
        TimeProvider timeProvider)
    {
        _restaurantRepository = restaurantRepository;
        _validator = validator;
        _currentUserService = currentUserService;
        _timeProvider = timeProvider;
    }

    /// <summary>Executes the restaurant detail query and maps the response.</summary>
    public async Task<RestaurantDetailDto> ExecuteAsync(
        GetRestaurantDetailRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var restaurant = await _restaurantRepository.GetPublicDetailAsync(request.RestaurantId, ct)
            ?? throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");

        ReviewDto? myReview = null;
        if (_currentUserService.UserId.HasValue)
        {
            var userReview = await _restaurantRepository.GetTravelerReviewForRestaurantAsync(
                request.RestaurantId,
                _currentUserService.UserId.Value,
                ct);

            if (userReview != null)
            {
                myReview = RestaurantDetailMapper.MapReview(userReview);
            }
        }

        IReadOnlyList<DTOs.Discovery.DishCardDto>? menu = null;
        if (!string.IsNullOrWhiteSpace(request.Include) &&
            request.Include.Contains("menu", StringComparison.OrdinalIgnoreCase))
        {
            var dishes = await _restaurantRepository.GetActiveMenuDishesAsync(
                request.RestaurantId,
                tagIds: null,
                includeUnavailable: true,
                ct);

            menu = dishes.Select(dish => RestaurantDetailMapper.MapDishCard(dish, restaurant)).ToArray();
        }

        decimal? distanceKm = null;
        if (request.Lat.HasValue && request.Lng.HasValue)
        {
            distanceKm = RestaurantDetailMapper.CalculateHaversineDistance(
                request.Lat.Value,
                request.Lng.Value,
                restaurant.Latitude,
                restaurant.Longitude);
        }

        return RestaurantDetailMapper.MapDetail(restaurant, _timeProvider, distanceKm, myReview, menu);
    }
}
