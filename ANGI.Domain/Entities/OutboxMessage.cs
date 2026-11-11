using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class OutboxMessage : BaseEntity<long>, IHasCreatedAt
    {
        public Guid EventUuid { get; set; }
        public OutboxEventType EventType { get; set; }
        public string AggregateKey { get; set; } = null!;
        public string Payload { get; set; } = null!;
        public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
        public short AttemptCount { get; set; }
        public DateTime NextAttemptAt { get; set; }
        public DateTime? LockedUntil { get; set; }
        public string? LastError { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ProcessedAt { get; set; }
    }
}
