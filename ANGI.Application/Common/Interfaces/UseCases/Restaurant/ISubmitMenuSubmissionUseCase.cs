using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Submits an editable owner menu draft for moderator review.</summary>
public interface ISubmitMenuSubmissionUseCase
{
    /// <summary>Executes OWN-17 for the authenticated restaurant owner.</summary>
    Task<MenuSubmissionDto> ExecuteAsync(
        long submissionId,
        SubmitMenuSubmissionRequestDto request,
        CancellationToken ct);
}
