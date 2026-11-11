using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Coordinates business rules for REST-02 restaurant detail retrieval.</summary>
public interface IGetRestaurantDetailUseCase
{
    /// <summary>Executes the restaurant detail retrieval operation.</summary>
    Task<RestaurantDetailDto> ExecuteAsync(GetRestaurantDetailRequestDto request, CancellationToken ct);
}
