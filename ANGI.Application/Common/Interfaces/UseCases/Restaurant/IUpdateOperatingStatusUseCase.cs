using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Changes the authenticated owner's restaurant operating status (OWN-05).</summary>
public interface IUpdateOperatingStatusUseCase
{
    /// <summary>
    /// Saves the new status and writes RESTAURANT_OPERATING_STATUS_CHANGED when it changes. Raises 409
    /// RESTAURANT_SUSPENDED when a suspended restaurant is reopened and 404 RESTAURANT_NOT_FOUND without a restaurant.
    /// </summary>
    Task<UpdateOperatingStatusResponseDto> ExecuteAsync(UpdateOperatingStatusRequestDto request, CancellationToken ct);
}
