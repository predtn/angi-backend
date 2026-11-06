using System.Text.Json;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.DTOs.Administration;

/// <summary>Contains ADM-A1 filter and pagination parameters for the system audit log list.</summary>
public sealed class GetSystemAuditLogsRequestDto
{
    /// <summary>Gets or sets the optional actor user id filter (audit_logs.actor_id).</summary>
    public int? ActorId { get; set; }

    /// <summary>Gets or sets the optional subject user id filter (audit_logs.subject_user_id).</summary>
    public int? SubjectUserId { get; set; }

    /// <summary>Gets or sets the optional actor role filter (TRAVELER, RESTAURANT_OWNER, MOD, ADMIN).</summary>
    public string? ActorRole { get; set; }

    /// <summary>Gets or sets the optional action code filter (sheet Enum audit_logs.action).</summary>
    public string? Action { get; set; }

    /// <summary>Gets or sets the optional entity type filter (sheet Enum audit_logs.entityType).</summary>
    public string? EntityType { get; set; }

    /// <summary>Gets or sets the optional entity id filter (audit_logs.entity_id).</summary>
    public long? EntityId { get; set; }

    /// <summary>Gets or sets the optional lower bound (inclusive) of created_at.</summary>
    public DateTime? From { get; set; }

    /// <summary>Gets or sets the optional upper bound (inclusive) of created_at.</summary>
    public DateTime? To { get; set; }

    /// <summary>Gets or sets the 1-based page number.</summary>
    public int Page { get; set; } = 1;

    /// <summary>Gets or sets the number of items per page (1..100, default 20).</summary>
    public int PageSize { get; set; } = 20;
}

/// <summary>Contains every AuditLog field returned by ADM-A2, including old and new values.</summary>
public sealed class AuditLogDetailDto
{
    public long Id { get; init; }
    public AuditActorDto? Actor { get; init; }
    public string Action { get; init; } = string.Empty;
    public string? EntityType { get; init; }
    public long? EntityId { get; init; }
    public UserSummaryDto? SubjectUser { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>Gets the JSON object of columns before the change, or null.</summary>
    public JsonElement? OldValues { get; init; }

    /// <summary>Gets the JSON object of columns after the change, or null.</summary>
    public JsonElement? NewValues { get; init; }

    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
}
