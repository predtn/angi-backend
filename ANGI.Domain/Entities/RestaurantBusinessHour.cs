using ANGI.Domain.Common;

namespace ANGI.Domain.Entities
{
    public class RestaurantBusinessHour : BaseEntity<long>
    {
        public long RestaurantId { get; set; }
        public short DayOfWeek { get; set; }
        public TimeOnly OpenTime { get; set; }
        public TimeOnly CloseTime { get; set; }

        public Restaurant Restaurant { get; set; } = null!;
    }
}
