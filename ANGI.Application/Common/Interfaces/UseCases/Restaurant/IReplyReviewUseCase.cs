using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Defines the contract for OWN-20 replying to a review of the authenticated owner's restaurant.</summary>
public interface IReplyReviewUseCase
{
    Task<ReviewReplyDto> ExecuteAsync(long reviewId, ReplyReviewRequestDto request, CancellationToken ct);
}
