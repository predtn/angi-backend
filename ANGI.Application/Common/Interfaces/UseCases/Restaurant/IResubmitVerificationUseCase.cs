using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Defines the OWN-08 restaurant verification resubmission workflow.</summary>
public interface IResubmitVerificationUseCase
{
    /// <summary>
    /// Creates a new pending submission that points back at <paramref name="verificationId"/>. Raises 404
    /// RESTAURANT_NOT_FOUND or VERIFICATION_NOT_FOUND (not a submission of the caller's restaurant), and 409
    /// VERIFICATION_NOT_RESUBMITTABLE when the previous submission is not rejected / needs_more_info or is not the
    /// latest one.
    /// </summary>
    Task<VerificationDto> ExecuteAsync(
        long verificationId,
        ResubmitVerificationRequestDto request,
        CancellationToken ct);
}
