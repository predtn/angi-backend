using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.UseCases.Administration.GetModPermissions;

/// <summary>Implements ADM-P2 by calculating one active MOD account's effective permissions.</summary>
public sealed class GetModPermissionsUseCase : IGetModPermissionsUseCase
{
    private readonly IAdministrationPermissionRepository _repository;

    /// <summary>Initializes the MOD permission query with its repository.</summary>
    public GetModPermissionsUseCase(IAdministrationPermissionRepository repository)
    {
        _repository = repository;
    }

    /// <summary>Returns defaults, overrides, and effective permissions or MOD_NOT_FOUND.</summary>
    public async Task<ModPermissionsDto> ExecuteAsync(int userId, CancellationToken ct)
    {
        var snapshot = await _repository.GetModPermissionsAsync(userId, ct)
            ?? throw new NotFoundException("MOD_NOT_FOUND", "Không tìm thấy tài khoản Mod.");

        return AdministrationPermissionMapper.ToModPermissionsDto(snapshot.RoleDefaults, snapshot.Overrides);
    }
}
