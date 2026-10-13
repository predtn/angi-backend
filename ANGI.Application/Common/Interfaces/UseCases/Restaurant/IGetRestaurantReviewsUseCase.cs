using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Coordinates business rules for REST-04 restaurant reviews retrieval.</summary>
public interface IGetRestaurantReviewsUseCase
{
    /// <summary>Executes the restaurant reviews retrieval operation.</summary>
    Task<RestaurantReviewsDto> ExecuteAsync(GetRestaurantReviewsRequestDto request, CancellationToken ct);
}
