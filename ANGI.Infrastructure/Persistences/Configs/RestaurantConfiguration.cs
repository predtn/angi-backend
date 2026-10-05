using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class RestaurantConfiguration : IEntityTypeConfiguration<Restaurant>
    {
        public void Configure(EntityTypeBuilder<Restaurant> builder)
        {
            builder.ToTable("restaurants", table =>
                table.HasCheckConstraint("ck_restaurants_price_level", "price_level BETWEEN 1 AND 4"));

            builder.Property(x => x.Name).HasMaxLength(200);
            builder.Property(x => x.Slug).HasMaxLength(220);
            builder.Property(x => x.Phone).HasMaxLength(20);
            builder.Property(x => x.Email).HasColumnType("citext");
            builder.Property(x => x.AddressLine).HasMaxLength(255);
            builder.Property(x => x.Ward).HasMaxLength(100);
            builder.Property(x => x.District).HasMaxLength(100);
            builder.Property(x => x.ProvinceName).HasMaxLength(100);
            builder.Property(x => x.Latitude).HasPrecision(9, 6);
            builder.Property(x => x.Longitude).HasPrecision(9, 6);
            builder.Property(x => x.VerificationStatus).HasMaxLength(20).HasDefaultValue(RestaurantVerificationStatus.Unverified);
            builder.Property(x => x.OperatingStatus).HasMaxLength(20).HasDefaultValue(RestaurantOperatingStatus.Open);
            builder.Property(x => x.ModerationStatus).HasMaxLength(20).HasDefaultValue(RestaurantModerationStatus.Visible);
            builder.Property(x => x.RatingAvg).HasPrecision(3, 2).HasDefaultValue(0m);
            builder.Property(x => x.RatingCount).HasDefaultValue(0);

            builder.HasIndex(x => x.OwnerId).IsUnique().HasFilter("deleted_at IS NULL");
            builder.HasIndex(x => x.Slug).IsUnique().HasFilter("deleted_at IS NULL");

            builder.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId);
            builder.HasOne(x => x.CoverMedia).WithMany().HasForeignKey(x => x.CoverMediaId);
        }
    }
}
