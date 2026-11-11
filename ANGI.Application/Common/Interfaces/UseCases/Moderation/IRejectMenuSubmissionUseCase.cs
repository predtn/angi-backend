using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.Common.Models;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines the RejectMenuSubmission moderation operation.</summary>
public interface IRejectMenuSubmissionUseCase
{
    /// <summary>Executes the operation with caller cancellation.</summary>
    Task<MenuSubmissionDto> ExecuteAsync(long submissionId, RejectMenuSubmissionRequestDto request, CancellationToken ct);
}
