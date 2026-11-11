using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines the MOD-U5 use case for viewing audit logs related to a specific user.</summary>
public interface IGetUserAuditLogsUseCase
{
    /// <summary>Retrieves a paginated list of audit logs where the user is the actor or subject.</summary>
    Task<Paged<AuditLogDto>> ExecuteAsync(int userId, GetUserAuditLogsRequestDto request, CancellationToken ct);
}
