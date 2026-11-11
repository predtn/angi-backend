using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class Dish : BaseEntity<int>, IHasCreatedAt, IHasUpdatedAt
    {
        public long RestaurantId { get; set; }
        public string Name { get; set; } = null!;
        public string? Description { get; set; }
        public DishForm DishForm { get; set; } = DishForm.Other;
        public int LikeCount { get; set; }
        public int DislikeCount { get; set; }
        public decimal Price { get; set; }
        public long? CoverMediaId { get; set; }
        public bool IsAvailable { get; set; } = true;
        public DishStatus Status { get; set; } = DishStatus.Active;
        public short SortOrder { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public Restaurant Restaurant { get; set; } = null!;
        public MediaFile? CoverMedia { get; set; }
        public ICollection<DishTag> DishTags { get; set; } = new List<DishTag>();
        public ICollection<DishImage> Images { get; set; } = new List<DishImage>();
    }
}
