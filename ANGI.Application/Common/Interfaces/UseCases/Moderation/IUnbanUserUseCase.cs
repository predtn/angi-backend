using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>MOD-U4: Lifts an active ban or suspension from a user account.</summary>
public interface IUnbanUserUseCase
{
    /// <summary>Executes the unban use case, restoring user status to active and recording sanction lift.</summary>
    Task<AdminUserCardDto> ExecuteAsync(int userId, UnbanUserRequestDto request, CancellationToken ct);
}
