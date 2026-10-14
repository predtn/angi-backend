using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Defines the OWN-06 restaurant verification submission workflow.</summary>
public interface ISubmitVerificationUseCase
{
    /// <summary>
    /// Creates the authenticated owner's first verification submission. Raises 409 VERIFICATION_ALREADY_PENDING,
    /// RESTAURANT_ALREADY_VERIFIED or VERIFICATION_RESUBMIT_REQUIRED (rejected: resubmit through OWN-08).
    /// </summary>
    Task<VerificationDto> ExecuteAsync(SubmitVerificationRequestDto request, CancellationToken ct);
}
