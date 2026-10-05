namespace ANGI.Domain.Entities
{
    public class RestaurantImage
    {
        public long RestaurantId { get; set; }
        public long MediaId { get; set; }
        public short SortOrder { get; set; }

        public Restaurant Restaurant { get; set; } = null!;
        public MediaFile Media { get; set; } = null!;
    }
}
