using ANGI.Application.DTOs.Auth;

namespace ANGI.Application.Common.Interfaces.UseCases.Auth
{
    /// <summary>Defines the refresh-token rotation operation.</summary>
    public interface IRefreshTokenUseCase
    {
        /// <summary>Revokes the current token and issues a replacement pair when the session is valid.</summary>
        Task<AuthResultDto> ExecuteAsync(RefreshTokenRequestDto request, CancellationToken ct);
    }
}
