using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.UseCases.Discovery.GetDishDetail;

/// <summary>Implements DISH-02: a public dish with the restaurant that serves it.</summary>
public sealed class GetDishDetailUseCase : IGetDishDetailUseCase
{
    private readonly IDishRepository _dishRepository;
    private readonly TimeProvider _timeProvider;

    public GetDishDetailUseCase(IDishRepository dishRepository, TimeProvider timeProvider)
    {
        _dishRepository = dishRepository;
        _timeProvider = timeProvider;
    }

    public async Task<DishDetailDto> ExecuteAsync(int dishId, CancellationToken ct)
    {
        var dish = await _dishRepository.GetPublicDetailAsync(dishId, ct)
            ?? throw new NotFoundException("DISH_NOT_FOUND", "Không tìm thấy món ăn.");

        // DISH-02 does not go through reco and takes no lat/lng: rank and distanceKm stay null.
        return DishDetailMapper.Map(dish, _timeProvider);
    }
}
