using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class RoadmapItem : BaseEntity<long>, IHasCreatedAt, IHasUpdatedAt
    {
        public long RoadmapDayId { get; set; }
        public int DishId { get; set; }
        public MealSlot MealSlot { get; set; }
        public short SortOrder { get; set; }
        public RoadmapItemSource Source { get; set; }
        public Guid? RecoRequestId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public RoadmapDay RoadmapDay { get; set; } = null!;
        public Dish Dish { get; set; } = null!;
    }
}
