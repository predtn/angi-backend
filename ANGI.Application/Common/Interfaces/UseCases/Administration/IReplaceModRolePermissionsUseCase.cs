using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.Common.Interfaces.UseCases.Administration;

/// <summary>Replaces the ADM-P4 default permission set of the MOD role.</summary>
public interface IReplaceModRolePermissionsUseCase
{
    /// <summary>Validates and replaces all MOD role defaults.</summary>
    Task<ReplaceModRolePermissionsResponseDto> ExecuteAsync(
        ReplaceModRolePermissionsRequestDto request,
        CancellationToken ct);
}
