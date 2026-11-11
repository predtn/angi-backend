namespace ANGI.Domain.Entities
{
    public class ReviewImage
    {
        public long ReviewId { get; set; }
        public long MediaId { get; set; }

        public RestaurantReview Review { get; set; } = null!;
        public MediaFile Media { get; set; } = null!;
    }
}
