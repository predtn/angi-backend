using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.UseCases.Administration.GetSystemAuditLogDetail;

/// <summary>Implements ADM-A2 (F-64) to view one audit log record with old and new values.</summary>
public sealed class GetSystemAuditLogDetailUseCase(IAdministrationAuditLogRepository repository) : IGetSystemAuditLogDetailUseCase
{
    /// <inheritdoc />
    public async Task<AuditLogDetailDto> ExecuteAsync(long auditLogId, CancellationToken ct)
    {
        var record = auditLogId > 0 ? await repository.GetAuditLogByIdAsync(auditLogId, ct) : null;
        if (record is null)
        {
            throw new NotFoundException("AUDIT_LOG_NOT_FOUND", "Không tìm thấy bản ghi audit log.");
        }

        return new AuditLogDetailDto
        {
            Id = record.Id,
            Actor = record.Actor,
            Action = record.Action,
            EntityType = record.EntityType,
            EntityId = record.EntityId,
            SubjectUser = record.SubjectUser,
            CreatedAt = record.CreatedAt,
            OldValues = ParseJson(record.OldValues),
            NewValues = ParseJson(record.NewValues),
            IpAddress = record.IpAddress,
            UserAgent = record.UserAgent
        };
    }

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(json);
        }
    }
}
