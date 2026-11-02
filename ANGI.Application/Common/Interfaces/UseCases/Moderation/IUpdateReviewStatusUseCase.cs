using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>MOD-F2: Updates review moderation status.</summary>
public interface IUpdateReviewStatusUseCase
{
    Task<UpdateReviewStatusResponseDto> ExecuteAsync(long reviewId, UpdateReviewStatusRequestDto request, CancellationToken ct);
}
