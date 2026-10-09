using System.Text.Json;
using System.Text.Json.Serialization;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Audit;
using ANGI.Domain.Entities;
using ANGI.Infrastructure.Persistences;

namespace ANGI.Infrastructure.Services.Audit
{
    /// <summary>Adds audit_logs rows to the context; the use case's Unit of Work saves them.</summary>
    public sealed class AuditLogService : IAuditLogService
    {
        // audit_logs.user_agent is varchar(500) (Data Dictionary).
        private const int UserAgentMaxLength = 500;

        // Keys and enum values as in the database: {"status": "suspended", "suspended_until": "..."}.
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
        };

        private readonly ANGIContext _context;
        private readonly ICurrentUserService _currentUserService;

        /// <summary>Initializes audit tracking with the scoped context and current request identity.</summary>
        public AuditLogService(ANGIContext context, ICurrentUserService currentUserService)
        {
            _context = context;
            _currentUserService = currentUserService;
        }

        /// <summary>Adds a validated audit entry to the current Unit of Work without saving it immediately.</summary>
        public void Add(AuditLogEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (!AuditActions.All.Contains(entry.Action))
            {
                throw new ArgumentException($"Unknown audit action '{entry.Action}'; use AuditActions.", nameof(entry));
            }

            if (!AuditEntityTypes.All.Contains(entry.EntityType))
            {
                throw new ArgumentException($"Unknown audit entity type '{entry.EntityType}'; use AuditEntityTypes.", nameof(entry));
            }

            // An explicit actor (login, register) wins; otherwise the token's user, or null for a background job.
            var hasExplicitActor = entry.ActorId is not null || entry.ActorUser is not null;
            var userAgent = _currentUserService.UserAgent;

            _context.AuditLogs.Add(new AuditLog
            {
                ActorId = hasExplicitActor ? entry.ActorId : _currentUserService.UserId,
                Actor = entry.ActorUser,
                ActorRole = hasExplicitActor ? entry.ActorRole : _currentUserService.Role,
                Action = entry.Action,
                EntityType = entry.EntityType,
                EntityId = entry.EntityId,
                SubjectUserId = entry.SubjectUserId,
                SubjectUser = entry.SubjectUser,
                OldValues = Serialize(entry.OldValues),
                NewValues = Serialize(entry.NewValues),
                IpAddress = _currentUserService.IpAddress,
                UserAgent = string.IsNullOrWhiteSpace(userAgent)
                    ? null
                    : userAgent.Length > UserAgentMaxLength ? userAgent[..UserAgentMaxLength] : userAgent
            });
        }

        /// <summary>Serializes changed columns with database-compatible snake_case names and enum values.</summary>
        private static string? Serialize(object? values) =>
            values is null ? null : JsonSerializer.Serialize(values, values.GetType(), _jsonOptions);
    }
}
