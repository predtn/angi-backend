using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.Common.Interfaces.UseCases.Discovery;

/// <summary>Returns the public detail of a dish with its restaurant (DISH-02).</summary>
public interface IGetDishDetailUseCase
{
    /// <summary>Raises 404 DISH_NOT_FOUND when the dish does not exist, is hidden or removed, or its restaurant is not public.</summary>
    Task<DishDetailDto> ExecuteAsync(int dishId, CancellationToken ct);
}
