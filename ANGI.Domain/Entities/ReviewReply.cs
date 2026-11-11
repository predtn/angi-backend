using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class ReviewReply : IHasCreatedAt, IHasUpdatedAt
    {
        public long ReviewId { get; set; }
        public int ReplierId { get; set; }
        public string Content { get; set; } = null!;
        public ContentStatus Status { get; set; } = ContentStatus.Visible;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public RestaurantReview Review { get; set; } = null!;
    }
}
