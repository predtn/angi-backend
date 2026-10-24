using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.Common.Interfaces.UseCases.Administration;

/// <summary>Loads the ADM-P1 permission catalog.</summary>
public interface IGetPermissionCatalogUseCase
{
    /// <summary>Returns all permissions and current MOD and ADMIN defaults.</summary>
    Task<PermissionCatalogDto> ExecuteAsync(CancellationToken ct);
}
