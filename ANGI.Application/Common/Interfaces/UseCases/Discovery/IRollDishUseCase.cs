using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.Common.Interfaces.UseCases.Discovery;

/// <summary>Rolls one dish for a Traveler (DISH-03).</summary>
public interface IRollDishUseCase
{
    /// <summary>Raises 404 NO_CANDIDATE when no dish matches the filters.</summary>
    Task<RollDishResponseDto> ExecuteAsync(RollDishRequestDto request, CancellationToken ct);
}
