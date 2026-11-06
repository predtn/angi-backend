using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Administration;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.Repositories.Administration;

/// <summary>Provides read-only queries over audit_logs for administrators.</summary>
public interface IAdministrationAuditLogRepository
{
    /// <summary>Lists filtered audit logs, newest first.</summary>
    Task<Paged<AuditLogDto>> GetAuditLogsAsync(GetSystemAuditLogsRequestDto request, CancellationToken ct);

    /// <summary>Gets one audit log with raw old/new values, or null when it does not exist.</summary>
    Task<AuditLogDetailRecord?> GetAuditLogByIdAsync(long auditLogId, CancellationToken ct);
}
