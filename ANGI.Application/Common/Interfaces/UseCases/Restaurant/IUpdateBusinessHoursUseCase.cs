using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Replaces the authenticated owner's weekly business hours (OWN-04).</summary>
public interface IUpdateBusinessHoursUseCase
{
    /// <summary>
    /// Validates the schedule and saves it in place of the current one. Raises 400 INVALID_BUSINESS_HOURS for
    /// overlapping intervals and 404 RESTAURANT_NOT_FOUND when the owner has not registered a restaurant.
    /// </summary>
    Task<UpdateBusinessHoursResponseDto> ExecuteAsync(UpdateBusinessHoursRequestDto request, CancellationToken ct);
}
