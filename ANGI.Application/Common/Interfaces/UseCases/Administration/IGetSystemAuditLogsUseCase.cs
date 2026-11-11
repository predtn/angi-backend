using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Administration;

/// <summary>Defines the ADM-A1 use case for listing system audit logs.</summary>
public interface IGetSystemAuditLogsUseCase
{
    /// <summary>Returns a filtered, paginated audit log list, newest first.</summary>
    Task<Paged<AuditLogDto>> ExecuteAsync(GetSystemAuditLogsRequestDto request, CancellationToken ct);
}
