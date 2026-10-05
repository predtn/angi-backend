using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class UserSanction : BaseEntity<long>
    {
        public int UserId { get; set; }
        public SanctionType Type { get; set; }
        public string Reason { get; set; } = null!;
        public long? ReportId { get; set; }
        public int IssuedBy { get; set; }
        public DateTime StartsAt { get; set; }
        public DateTime? EndsAt { get; set; }
        public int? LiftedBy { get; set; }
        public DateTime? LiftedAt { get; set; }
        public string? LiftReason { get; set; }
    }
}
