using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Cancels a draft or pending owner menu submission.</summary>
public interface ICancelMenuSubmissionUseCase
{
    /// <summary>Executes OWN-18.</summary>
    Task<MenuSubmissionDto> ExecuteAsync(long submissionId, CancellationToken ct);
}
