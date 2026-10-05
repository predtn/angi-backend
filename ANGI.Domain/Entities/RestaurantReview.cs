using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class RestaurantReview : BaseEntity<long>, IHasCreatedAt
    {
        public long RestaurantId { get; set; }
        public int UserId { get; set; }
        public short Rating { get; set; }
        public string? Content { get; set; }
        public long? RoadmapItemId { get; set; }
        public ContentStatus Status { get; set; } = ContentStatus.Visible;
        public DateTime CreatedAt { get; set; }

        public Restaurant Restaurant { get; set; } = null!;
        public User User { get; set; } = null!;
        public ICollection<ReviewImage> Images { get; set; } = new List<ReviewImage>();
        public ReviewReply? Reply { get; set; }
    }
}
