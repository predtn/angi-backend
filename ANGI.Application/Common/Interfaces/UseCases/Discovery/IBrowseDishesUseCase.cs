using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.Common.Interfaces.UseCases.Discovery;

/// <summary>Browses and searches dishes ranked by reco (DISH-01).</summary>
public interface IBrowseDishesUseCase
{
    /// <summary>Raises 400 INVALID_CURSOR for a cursor that is malformed, expired or another user's.</summary>
    Task<BrowseDishesResponseDto> ExecuteAsync(BrowseDishesRequestDto request, CancellationToken ct);
}
