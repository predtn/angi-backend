using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Updates fields of the authenticated owner's restaurant profile.</summary>
public interface IUpdateOwnerRestaurantUseCase
{
    /// <summary>Applies an OWN-03 partial update and returns the complete owner profile.</summary>
    Task<OwnerRestaurantDto> ExecuteAsync(UpdateRestaurantRequestDto request, CancellationToken ct);
}
