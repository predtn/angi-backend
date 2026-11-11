using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines MOD-V2 verification detail retrieval.</summary>
public interface IGetVerificationReviewUseCase
{
    /// <summary>Returns one verification, its restaurant, and its preceding submission when present.</summary>
    Task<VerificationReviewDetailDto> ExecuteAsync(long verificationId, CancellationToken ct);
}
