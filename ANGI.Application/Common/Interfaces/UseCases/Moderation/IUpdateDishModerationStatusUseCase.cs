using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines the UpdateDishModerationStatus moderation operation.</summary>
public interface IUpdateDishModerationStatusUseCase
{
    /// <summary>Executes the operation with authorization and propagates request cancellation.</summary>
    Task<UpdateDishModerationStatusResponseDto> ExecuteAsync(int dishId, UpdateDishModerationStatusRequestDto request, CancellationToken ct);
}
