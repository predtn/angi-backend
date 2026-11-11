using ANGI.Application.DTOs.Auth;

namespace ANGI.Application.Common.Interfaces.UseCases.Auth
{
    /// <summary>Defines the AUTH-09 operation that consumes a reset token and replaces a password.</summary>
    public interface IResetPasswordUseCase
    {
        /// <summary>Changes the password and revokes every active session when the reset token is valid.</summary>
        Task ExecuteAsync(ResetPasswordRequestDto request, CancellationToken ct);
    }
}
