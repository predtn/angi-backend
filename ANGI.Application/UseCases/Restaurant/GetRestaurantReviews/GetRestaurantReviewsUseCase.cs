using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Restaurant.GetRestaurantReviews;

/// <summary>Implements REST-04 by returning paginated visible reviews and rating breakdown for a restaurant.</summary>
public sealed class GetRestaurantReviewsUseCase : IGetRestaurantReviewsUseCase
{
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly IValidator<GetRestaurantReviewsRequestDto> _validator;

    public GetRestaurantReviewsUseCase(
        IRestaurantRepository restaurantRepository,
        IValidator<GetRestaurantReviewsRequestDto> validator)
    {
        _restaurantRepository = restaurantRepository;
        _validator = validator;
    }

    /// <summary>Executes the restaurant reviews query and returns the paginated result.</summary>
    public async Task<RestaurantReviewsDto> ExecuteAsync(
        GetRestaurantReviewsRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var exists = await _restaurantRepository.ExistsAndActiveAsync(request.RestaurantId, ct);
        if (!exists)
        {
            throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");
        }

        var (items, totalItems, ratingBreakdown, ratingAvg, ratingCount) = await _restaurantRepository.GetReviewsAsync(
            request.RestaurantId,
            request.Rating,
            request.HasImage,
            request.Sort,
            request.Page,
            request.PageSize,
            ct);

        var totalPages = (int)Math.Ceiling((double)totalItems / request.PageSize);
        var mappedItems = items.Select(RestaurantDetailMapper.MapReview).ToArray();

        return new RestaurantReviewsDto
        {
            Items = mappedItems,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalItems = totalItems,
            TotalPages = totalPages,
            RatingAvg = ratingAvg,
            RatingCount = ratingCount,
            RatingBreakdown = ratingBreakdown
        };
    }
}
