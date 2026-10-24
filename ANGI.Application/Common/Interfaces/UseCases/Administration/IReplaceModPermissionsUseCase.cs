using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.Common.Interfaces.UseCases.Administration;

/// <summary>Replaces the ADM-P3 permission overrides of one MOD account.</summary>
public interface IReplaceModPermissionsUseCase
{
    /// <summary>Validates and replaces all overrides, then returns the resulting effective permissions.</summary>
    Task<ModPermissionsDto> ExecuteAsync(
        int userId,
        ReplaceModPermissionsRequestDto request,
        CancellationToken ct);
}
