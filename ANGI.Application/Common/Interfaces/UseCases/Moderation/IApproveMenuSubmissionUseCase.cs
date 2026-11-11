using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.Common.Models;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines the ApproveMenuSubmission moderation operation.</summary>
public interface IApproveMenuSubmissionUseCase
{
    /// <summary>Executes the operation with caller cancellation.</summary>
    Task<ApproveMenuSubmissionResponseDto> ExecuteAsync(long submissionId, ApproveMenuSubmissionRequestDto request, CancellationToken ct);
}
