using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Models.Administration;

/// <summary>Carries one audit_logs row from the repository, with jsonb columns still as raw JSON text.</summary>
public sealed class AuditLogDetailRecord
{
    public long Id { get; init; }
    public AuditActorDto? Actor { get; init; }
    public string Action { get; init; } = string.Empty;
    public string? EntityType { get; init; }
    public long? EntityId { get; init; }
    public UserSummaryDto? SubjectUser { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? OldValues { get; init; }
    public string? NewValues { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
}
