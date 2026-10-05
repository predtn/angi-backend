using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class MenuSubmission : BaseEntity<long>, IHasCreatedAt
    {
        public long RestaurantId { get; set; }
        public int CreatedBy { get; set; }
        public MenuSubmissionStatus Status { get; set; } = MenuSubmissionStatus.Draft;
        public string? OwnerNote { get; set; }
        public DateTime? SubmittedAt { get; set; }
        public int? ReviewedBy { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? ReviewNote { get; set; }
        public DateTime CreatedAt { get; set; }

        public Restaurant Restaurant { get; set; } = null!;
        public ICollection<MenuSubmissionItem> Items { get; set; } = new List<MenuSubmissionItem>();
    }
}
