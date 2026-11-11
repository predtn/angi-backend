using ANGI.Application.DTOs.Auth;

namespace ANGI.Application.Common.Interfaces.UseCases.Auth
{
    /// <summary>Defines the AUTH-02 email-verification operation.</summary>
    public interface IVerifyEmailUseCase
    {
        /// <summary>Consumes a valid email-verification token and activates its user account.</summary>
        Task<VerifyEmailResponseDto> ExecuteAsync(VerifyEmailRequestDto request, CancellationToken ct);
    }
}
