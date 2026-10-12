using System.Net;
using ANGI.Domain.Common;

namespace ANGI.Domain.Entities
{
    public class AuditLog : BaseEntity<long>, IHasCreatedAt
    {
        public int? ActorId { get; set; }
        public string? ActorRole { get; set; }
        public string Action { get; set; } = null!;
        public string? EntityType { get; set; }
        public long? EntityId { get; set; }
        public int? SubjectUserId { get; set; }
        public string? OldValues { get; set; }
        public string? NewValues { get; set; }
        public IPAddress? IpAddress { get; set; }
        public string? UserAgent { get; set; }
        public DateTime CreatedAt { get; set; }

        public User? Actor { get; set; }
        public User? SubjectUser { get; set; }
    }
}
