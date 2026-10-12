using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Loads the authenticated owner's complete restaurant profile.</summary>
public interface IGetOwnerRestaurantUseCase
{
    /// <summary>Returns the OWN-02 owner profile or raises RESTAURANT_NOT_FOUND.</summary>
    Task<OwnerRestaurantDto> ExecuteAsync(CancellationToken ct);
}
