using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.Common.Interfaces.UseCases.Administration;

/// <summary>Defines ADM-M4 MOD display-name and status updates.</summary>
public interface IUpdateModUseCase
{
    /// <summary>Updates one MOD and revokes sessions when it is deactivated.</summary>
    Task<AdminUserCardDto> ExecuteAsync(int userId, UpdateModRequestDto request, CancellationToken ct);
}
