using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines the atomic MOD-V3 verification decision operation.</summary>
public interface IReviewVerificationUseCase
{
    /// <summary>Applies a decision and returns the reviewed verification.</summary>
    Task<VerificationDto> ExecuteAsync(
        long verificationId,
        ReviewVerificationRequestDto request,
        CancellationToken ct);
}
