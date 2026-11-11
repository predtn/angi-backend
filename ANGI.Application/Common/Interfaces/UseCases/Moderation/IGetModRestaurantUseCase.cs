using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines the GetModRestaurant moderation operation.</summary>
public interface IGetModRestaurantUseCase
{
    /// <summary>Executes the operation with authorization and propagates request cancellation.</summary>
    Task<ModRestaurantDetailDto> ExecuteAsync(long restaurantId, CancellationToken ct);
}
