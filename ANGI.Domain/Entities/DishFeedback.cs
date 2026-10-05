using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class DishFeedback : BaseEntity<long>, IHasCreatedAt
    {
        public Guid EventUuid { get; set; }
        public int UserId { get; set; }
        public int DishId { get; set; }
        public FeedbackValue Value { get; set; }
        public long? RoadmapItemId { get; set; }
        public Guid? RecoRequestId { get; set; }
        public DateTime CreatedAt { get; set; }

        public Dish Dish { get; set; } = null!;
    }
}
