using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Lists menu submission history for the authenticated owner.</summary>
public interface IListMenuSubmissionsUseCase
{
    /// <summary>Executes OWN-12.</summary>
    Task<Paged<MenuSubmissionDto>> ExecuteAsync(
        ListMenuSubmissionsRequestDto request,
        CancellationToken ct);
}
