using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    /// <summary>Configures weekly restaurant opening intervals.</summary>
    public class RestaurantBusinessHourConfiguration : IEntityTypeConfiguration<RestaurantBusinessHour>
    {
        /// <summary>Maps day constraints, interval uniqueness, and restaurant ownership.</summary>
        public void Configure(EntityTypeBuilder<RestaurantBusinessHour> builder)
        {
            builder.ToTable("restaurant_business_hours", table =>
                table.HasCheckConstraint("ck_restaurant_business_hours_day_of_week", "day_of_week BETWEEN 0 AND 6"));

            builder.HasIndex(x => new { x.RestaurantId, x.DayOfWeek, x.OpenTime }).IsUnique();

            builder.HasOne(x => x.Restaurant).WithMany(x => x.BusinessHours).HasForeignKey(x => x.RestaurantId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}
