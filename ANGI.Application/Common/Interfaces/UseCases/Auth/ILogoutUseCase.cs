using ANGI.Application.DTOs.Auth;

namespace ANGI.Application.Common.Interfaces.UseCases.Auth
{
    /// <summary>Defines the operation that logs out the current session.</summary>
    public interface ILogoutUseCase
    {
        /// <summary>Revokes the session when the refresh token belongs to the authenticated user.</summary>
        Task ExecuteAsync(LogoutRequestDto request, CancellationToken ct);
    }
}
