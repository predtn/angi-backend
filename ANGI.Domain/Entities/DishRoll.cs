using ANGI.Domain.Common;

namespace ANGI.Domain.Entities
{
    public class DishRoll : BaseEntity<long>
    {
        public int UserId { get; set; }
        public int DishId { get; set; }
        public Guid? RecoRequestId { get; set; }
        public DateTime RolledAt { get; set; }

        public Dish Dish { get; set; } = null!;
    }
}
