using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.UseCases.Restaurant.GetOwnerProfile;

/// <summary>Implements OWN-02 by loading the authenticated owner's restaurant profile.</summary>
public sealed class GetOwnerRestaurantUseCase : IGetOwnerRestaurantUseCase
{
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly ICloudinaryService _cloudinaryService;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the owner-profile query and response mapping dependencies.</summary>
    public GetOwnerRestaurantUseCase(
        IRestaurantRepository restaurantRepository,
        ICurrentUserService currentUserService,
        ICloudinaryService cloudinaryService,
        TimeProvider timeProvider)
    {
        _restaurantRepository = restaurantRepository;
        _currentUserService = currentUserService;
        _cloudinaryService = cloudinaryService;
        _timeProvider = timeProvider;
    }

    /// <summary>Loads and maps the owner restaurant or raises the documented not-found error.</summary>
    public async Task<OwnerRestaurantDto> ExecuteAsync(CancellationToken ct)
    {
        // STEP-01: Prepare ownerId and restaurant from restaurantRepository.GetOwnerProfileAsync.
        var ownerId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xem nhà hàng.");
        var restaurant = await _restaurantRepository.GetOwnerProfileAsync(ownerId, ct)
            ?? throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");

        // STEP-02: Return the result of OwnerRestaurantMapper.Map.
        return OwnerRestaurantMapper.Map(restaurant, _cloudinaryService, _timeProvider);
    }
}
