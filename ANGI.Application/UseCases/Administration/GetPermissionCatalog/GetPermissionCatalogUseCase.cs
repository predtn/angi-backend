using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.UseCases.Administration.GetPermissionCatalog;

/// <summary>Implements ADM-P1 by returning the permission catalog and role defaults.</summary>
public sealed class GetPermissionCatalogUseCase : IGetPermissionCatalogUseCase
{
    private readonly IAdministrationPermissionRepository _repository;

    /// <summary>Initializes the catalog query with its permission repository.</summary>
    public GetPermissionCatalogUseCase(IAdministrationPermissionRepository repository)
    {
        _repository = repository;
    }

    /// <summary>Loads and maps all permission definitions and current administrative role defaults.</summary>
    public async Task<PermissionCatalogDto> ExecuteAsync(CancellationToken ct)
    {
        var snapshot = await _repository.GetCatalogAsync(ct);
        return AdministrationPermissionMapper.ToCatalogDto(snapshot);
    }
}
