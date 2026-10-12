using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Updates availability of an active owner dish.</summary>
public interface IUpdateDishAvailabilityUseCase
{
    /// <summary>Executes OWN-10.</summary>
    Task<DishAvailabilityDto> ExecuteAsync(
        int dishId,
        UpdateDishAvailabilityRequestDto request,
        CancellationToken ct);
}
