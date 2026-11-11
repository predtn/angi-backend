namespace ANGI.Domain.Entities
{
    public class DishTag
    {
        public int DishId { get; set; }
        public short TagId { get; set; }

        public Dish Dish { get; set; } = null!;
        public Tag Tag { get; set; } = null!;
    }
}
