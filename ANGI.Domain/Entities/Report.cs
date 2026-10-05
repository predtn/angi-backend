using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class Report : BaseEntity<long>, IHasCreatedAt
    {
        public int ReporterId { get; set; }
        public ReportTargetType TargetType { get; set; }
        public long? RestaurantId { get; set; }
        public long? BlogId { get; set; }
        public ReportReasonCode ReasonCode { get; set; }
        public string? Description { get; set; }
        public string Evidence { get; set; } = "[]";
        public ReportStatus Status { get; set; } = ReportStatus.Pending;
        public int? HandledBy { get; set; }
        public DateTime? HandledAt { get; set; }
        public ResolutionAction? ResolutionAction { get; set; }
        public string? ResolutionNote { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
