using ANGI.Application.DTOs.Auth;

namespace ANGI.Application.Common.Interfaces.UseCases.Auth
{
    /// <summary>Defines the AUTH-08 password-reset request operation.</summary>
    public interface IForgotPasswordUseCase
    {
        /// <summary>Replaces outstanding reset tokens and sends a new link when the account exists.</summary>
        Task ExecuteAsync(ForgotPasswordRequestDto request, CancellationToken ct);
    }
}
