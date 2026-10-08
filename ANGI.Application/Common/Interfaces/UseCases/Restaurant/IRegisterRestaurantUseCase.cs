using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Registers the authenticated restaurant owner's first restaurant.</summary>
public interface IRegisterRestaurantUseCase
{
    /// <summary>Validates and creates a restaurant aggregate in its initial moderation states.</summary>
    Task<OwnerRestaurantDto> ExecuteAsync(RegisterRestaurantRequestDto request, CancellationToken ct);
}
