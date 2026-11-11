using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Loads the verification status and submission history for the authenticated owner's restaurant.</summary>
public interface IGetVerificationStatusUseCase
{
    /// <summary>Returns the OWN-07 verification status and history or raises RESTAURANT_NOT_FOUND.</summary>
    Task<GetVerificationStatusResponseDto> ExecuteAsync(CancellationToken ct);
}
