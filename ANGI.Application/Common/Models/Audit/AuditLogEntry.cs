namespace ANGI.Application.Common.Models.Audit
{
    /// <summary>One row to write to audit_logs (coding_rule.md §17).</summary>
    public sealed class AuditLogEntry
    {
        /// <summary>One of <see cref="AuditActions"/>.</summary>
        public required string Action { get; init; }

        /// <summary>One of <see cref="AuditEntityTypes"/>.</summary>
        public required string EntityType { get; init; }

        public long? EntityId { get; init; }

        public int? SubjectUserId { get; init; }

        /// <summary>Only the changed columns, e.g. <c>new { Status = UserStatus.Active }</c>.</summary>
        public object? OldValues { get; init; }

        /// <summary>Only the changed columns; a Mod's reason goes in <c>Reason</c>.</summary>
        public object? NewValues { get; init; }

        /// <summary>Set only when the request has no token yet (login, register); otherwise the token's user.</summary>
        public int? ActorId { get; init; }

        /// <summary>Set together with <see cref="ActorId"/>.</summary>
        public string? ActorRole { get; init; }
    }
}
