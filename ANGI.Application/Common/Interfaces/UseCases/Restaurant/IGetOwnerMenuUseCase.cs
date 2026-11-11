using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Loads the authenticated owner's active menu.</summary>
public interface IGetOwnerMenuUseCase
{
    /// <summary>Executes OWN-09.</summary>
    Task<OwnerMenuDto> ExecuteAsync(CancellationToken ct);
}
