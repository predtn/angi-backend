using ANGI.Application.DTOs.Auth;

namespace ANGI.Application.Common.Interfaces.UseCases.Auth
{
    /// <summary>Defines the email-and-password login operation.</summary>
    public interface ILoginUseCase
    {
        /// <summary>Validates credentials, creates a session, and returns the authentication result.</summary>
        Task<AuthResultDto> ExecuteAsync(LoginRequestDto request, CancellationToken ct);
    }
}
