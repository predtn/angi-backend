using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines the MOD-U1 operation to list users for moderation.</summary>
public interface IGetUsersUseCase
{
    /// <summary>Executes the list query with validation and moderation authorization.</summary>
    Task<Paged<AdminUserCardDto>> ExecuteAsync(ListUsersRequestDto request, CancellationToken ct);
}
