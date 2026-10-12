using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.UseCases.Restaurant.GetOwnerMenu;

/// <summary>Implements OWN-09 by loading the current owner menu and open workflow.</summary>
public sealed class GetOwnerMenuUseCase : IGetOwnerMenuUseCase
{
    private readonly IRestaurantMenuRepository _repository;
    private readonly ICurrentUserService _currentUser;

    /// <summary>Initializes the owner menu query dependencies.</summary>
    public GetOwnerMenuUseCase(IRestaurantMenuRepository repository, ICurrentUserService currentUser)
    {
        _repository = repository;
        _currentUser = currentUser;
    }

    /// <summary>Loads and maps the owner's non-removed dishes in display order.</summary>
    public async Task<OwnerMenuDto> ExecuteAsync(CancellationToken ct)
    {
        var ownerId = _currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xem menu.");
        var restaurant = await _repository.GetOwnerMenuAsync(ownerId, ct)
            ?? throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");
        return RestaurantMenuMapper.MapOwnerMenu(restaurant);
    }
}
