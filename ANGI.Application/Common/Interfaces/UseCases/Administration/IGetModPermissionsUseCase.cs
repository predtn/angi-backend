using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.Common.Interfaces.UseCases.Administration;

/// <summary>Loads the ADM-P2 permissions of one MOD account.</summary>
public interface IGetModPermissionsUseCase
{
    /// <summary>Returns inherited, overridden, and effective permissions for the selected MOD.</summary>
    Task<ModPermissionsDto> ExecuteAsync(int userId, CancellationToken ct);
}
