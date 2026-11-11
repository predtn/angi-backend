using ANGI.Application.DTOs.Account;

namespace ANGI.Application.DTOs.Moderation;

/// <summary>Contains the audit list fields documented for MOD-R2.</summary>
public sealed class AuditLogDto
{
    public long Id { get; init; }
    public AuditActorDto? Actor { get; init; }
    public string Action { get; init; } = string.Empty;
    public string? EntityType { get; init; }
    public long? EntityId { get; init; }
    public UserSummaryDto? SubjectUser { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>Preserves the actor role captured when the audit event was written.</summary>
public sealed class AuditActorDto
{
    public int Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? Role { get; init; }
}
