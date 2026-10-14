using ANGI.Application.DTOs.Auth;

namespace ANGI.Application.Common.Interfaces.UseCases.Auth
{
    /// <summary>Defines the AUTH-05 Google sign-in workflow.</summary>
    public interface IGoogleLoginUseCase
    {
        /// <summary>Logs in a linked Google identity or creates and signs in its ANGI account.</summary>
        Task<GoogleLoginResponseDto> ExecuteAsync(GoogleLoginRequestDto request, CancellationToken ct);
    }
}
