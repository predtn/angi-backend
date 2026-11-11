using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines the UpdateRestaurantModerationStatus moderation operation.</summary>
public interface IUpdateRestaurantModerationStatusUseCase
{
    /// <summary>Executes the operation with authorization and propagates request cancellation.</summary>
    Task<UpdateRestaurantModerationStatusResponseDto> ExecuteAsync(long restaurantId, UpdateRestaurantModerationStatusRequestDto request, CancellationToken ct);
}
