using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class Restaurant : BaseEntity<long>, IHasCreatedAt, IHasUpdatedAt, ISoftDelete
    {
        public int OwnerId { get; set; }
        public string Name { get; set; } = null!;
        public string Slug { get; set; } = null!;
        public string? Description { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? Website { get; set; }
        public string AddressLine { get; set; } = null!;
        public string? Ward { get; set; }
        public string? District { get; set; }
        public string? ProvinceName { get; set; }
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
        public short? PriceLevel { get; set; }
        public long? CoverMediaId { get; set; }
        public RestaurantVerificationStatus VerificationStatus { get; set; } = RestaurantVerificationStatus.Unverified;
        public RestaurantOperatingStatus OperatingStatus { get; set; } = RestaurantOperatingStatus.Open;
        public RestaurantModerationStatus ModerationStatus { get; set; } = RestaurantModerationStatus.Visible;
        public decimal RatingAvg { get; set; }
        public int RatingCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }

        public User Owner { get; set; } = null!;
        public MediaFile? CoverMedia { get; set; }
        public ICollection<RestaurantImage> Images { get; set; } = new List<RestaurantImage>();
        public ICollection<RestaurantBusinessHour> BusinessHours { get; set; } = new List<RestaurantBusinessHour>();
        public ICollection<Dish> Dishes { get; set; } = new List<Dish>();
        public ICollection<RestaurantVerification> Verifications { get; set; } = new List<RestaurantVerification>();
    }
}
