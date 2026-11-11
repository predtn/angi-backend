namespace ANGI.Domain.Entities
{
    public class DishImage
    {
        public int DishId { get; set; }
        public long MediaId { get; set; }
        public short SortOrder { get; set; }

        public Dish Dish { get; set; } = null!;
        public MediaFile Media { get; set; } = null!;
    }
}
