using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.Common.Interfaces.UseCases.Administration;

/// <summary>Defines the ADM-A2 use case for viewing one audit log record.</summary>
public interface IGetSystemAuditLogDetailUseCase
{
    /// <summary>Returns the audit log with old and new values, or throws AUDIT_LOG_NOT_FOUND.</summary>
    Task<AuditLogDetailDto> ExecuteAsync(long auditLogId, CancellationToken ct);
}
