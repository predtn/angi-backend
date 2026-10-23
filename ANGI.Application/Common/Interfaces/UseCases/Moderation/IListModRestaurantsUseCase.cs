using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines the ListModRestaurants moderation operation.</summary>
public interface IListModRestaurantsUseCase
{
    /// <summary>Executes the operation with authorization and propagates request cancellation.</summary>
    Task<Paged<ModRestaurantCardDto>> ExecuteAsync(ListModRestaurantsRequestDto request, CancellationToken ct);
}
