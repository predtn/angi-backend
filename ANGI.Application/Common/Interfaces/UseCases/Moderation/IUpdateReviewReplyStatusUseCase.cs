using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>MOD-F3: Updates review owner reply moderation status.</summary>
public interface IUpdateReviewReplyStatusUseCase
{
    Task<UpdateReviewReplyStatusResponseDto> ExecuteAsync(long reviewId, UpdateReviewReplyStatusRequestDto request, CancellationToken ct);
}
