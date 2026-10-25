using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Restaurant.GetOwnerReviews;

/// <summary>Implements OWN-19 by returning paginated reviews for the authenticated owner's restaurant.</summary>
public sealed class GetOwnerReviewsUseCase : IGetOwnerReviewsUseCase
{
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IValidator<GetOwnerReviewsRequestDto> _validator;

    public GetOwnerReviewsUseCase(
        IRestaurantRepository restaurantRepository,
        ICurrentUserService currentUserService,
        IValidator<GetOwnerReviewsRequestDto> validator)
    {
        _restaurantRepository = restaurantRepository;
        _currentUserService = currentUserService;
        _validator = validator;
    }

    /// <summary>Executes the owner restaurant reviews query and returns the paginated result.</summary>
    public async Task<Paged<ReviewDto>> ExecuteAsync(
        GetOwnerReviewsRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var ownerId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để thực hiện thao tác này.");

        var restaurant = await _restaurantRepository.GetByOwnerIdAsync(ownerId, ct);
        if (restaurant == null)
        {
            throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");
        }

        var (items, totalItems) = await _restaurantRepository.GetOwnerReviewsAsync(
            restaurant.Id,
            request.Replied,
            request.Rating,
            request.Page,
            request.PageSize,
            ct);

        var totalPages = (int)Math.Ceiling((double)totalItems / request.PageSize);
        var mappedItems = items
            .Select(review => RestaurantDetailMapper.MapReview(review, includeAllReplies: true))
            .ToArray();

        return new Paged<ReviewDto>
        {
            Items = mappedItems,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalItems = totalItems,
            TotalPages = totalPages
        };
    }
}
