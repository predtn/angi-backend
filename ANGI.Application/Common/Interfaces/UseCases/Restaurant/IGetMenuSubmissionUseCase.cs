using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Loads one owner menu submission and its current-menu comparison.</summary>
public interface IGetMenuSubmissionUseCase
{
    /// <summary>Executes OWN-13.</summary>
    Task<MenuSubmissionDetailDto> ExecuteAsync(long submissionId, CancellationToken ct);
}
