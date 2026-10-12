using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Restaurant.GetRestaurantMenu;

/// <summary>Implements REST-03 by returning the active menu dishes for a restaurant.</summary>
public sealed class GetRestaurantMenuUseCase : IGetRestaurantMenuUseCase
{
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly IValidator<GetRestaurantMenuRequestDto> _validator;

    public GetRestaurantMenuUseCase(
        IRestaurantRepository restaurantRepository,
        IValidator<GetRestaurantMenuRequestDto> validator)
    {
        _restaurantRepository = restaurantRepository;
        _validator = validator;
    }

    /// <summary>Executes the restaurant menu query and returns the active dishes.</summary>
    public async Task<RestaurantMenuDto> ExecuteAsync(
        GetRestaurantMenuRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var exists = await _restaurantRepository.ExistsAndActiveAsync(request.RestaurantId, ct);
        if (!exists)
        {
            throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");
        }

        var dishes = await _restaurantRepository.GetActiveMenuDishesAsync(
            request.RestaurantId,
            request.TagIds,
            request.IncludeUnavailable,
            ct);

        var dishCards = dishes
            .Select(dish => RestaurantDetailMapper.MapDishCard(dish, dish.Restaurant))
            .ToArray();

        return new RestaurantMenuDto
        {
            RestaurantId = request.RestaurantId,
            Dishes = dishCards
        };
    }
}
