using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Coordinates business rules for REST-03 restaurant menu retrieval.</summary>
public interface IGetRestaurantMenuUseCase
{
    /// <summary>Executes the restaurant menu retrieval operation.</summary>
    Task<RestaurantMenuDto> ExecuteAsync(GetRestaurantMenuRequestDto request, CancellationToken ct);
}
