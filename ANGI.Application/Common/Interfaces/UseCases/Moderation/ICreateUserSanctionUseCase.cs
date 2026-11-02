using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Contract for MOD-U3: creating an account sanction (warn, suspend, ban).</summary>
public interface ICreateUserSanctionUseCase
{
    /// <summary>Validates, authorizes and applies a sanction against the specified non-staff user.</summary>
    Task<SanctionDto> ExecuteAsync(int userId, CreateUserSanctionRequestDto request, CancellationToken ct);
}
