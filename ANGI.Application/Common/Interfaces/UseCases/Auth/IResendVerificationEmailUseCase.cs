using ANGI.Application.DTOs.Auth;

namespace ANGI.Application.Common.Interfaces.UseCases.Auth
{
    /// <summary>Defines the AUTH-03 verification-email resend operation.</summary>
    public interface IResendVerificationEmailUseCase
    {
        /// <summary>Replaces outstanding verification tokens and sends a new link when the account is eligible.</summary>
        Task ExecuteAsync(ResendVerificationEmailRequestDto request, CancellationToken ct);
    }
}
