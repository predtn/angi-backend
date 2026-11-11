using ANGI.Application.DTOs.Account;

namespace ANGI.Application.Common.Interfaces.UseCases.Account;

/// <summary>Coordinates password changing and session revocation for an authenticated user (ME-03).</summary>
public interface IChangePasswordUseCase
{
    /// <summary>Validates request, verifies current password, updates password hash, revokes other sessions, and logs audit.</summary>
    Task ExecuteAsync(ChangePasswordRequestDto request, CancellationToken ct);
}
