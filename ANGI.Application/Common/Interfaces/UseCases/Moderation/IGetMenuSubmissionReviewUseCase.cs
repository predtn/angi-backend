using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.Common.Models;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines the GetMenuSubmissionReview moderation operation.</summary>
public interface IGetMenuSubmissionReviewUseCase
{
    /// <summary>Executes the operation with caller cancellation.</summary>
    Task<ModMenuSubmissionDetailDto> ExecuteAsync(long submissionId, CancellationToken ct);
}
