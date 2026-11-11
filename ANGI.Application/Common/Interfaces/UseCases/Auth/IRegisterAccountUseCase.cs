using ANGI.Application.DTOs.Auth;

namespace ANGI.Application.Common.Interfaces.UseCases.Auth;

/// <summary>Defines the public email-and-password registration operation.</summary>
public interface IRegisterAccountUseCase
{
    /// <summary>Creates a pending account and sends its one-time email verification token.</summary>
    Task<RegisterAccountResponseDto> ExecuteAsync(
        RegisterAccountRequestDto request,
        CancellationToken ct);
}
