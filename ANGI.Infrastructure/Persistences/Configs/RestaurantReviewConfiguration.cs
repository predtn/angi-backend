using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class RestaurantReviewConfiguration : IEntityTypeConfiguration<RestaurantReview>
    {
        public void Configure(EntityTypeBuilder<RestaurantReview> builder)
        {
            builder.ToTable("restaurant_reviews", table =>
                table.HasCheckConstraint("ck_restaurant_reviews_rating", "rating BETWEEN 1 AND 5"));

            builder.Property(x => x.Status).HasMaxLength(10).HasDefaultValue(ContentStatus.Visible);

            builder.HasIndex(x => new { x.RestaurantId, x.UserId }).IsUnique();

            builder.HasOne(x => x.Restaurant).WithMany().HasForeignKey(x => x.RestaurantId);
            builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            // Roadmap items are hard-deleted; the review stays
            builder.HasOne<RoadmapItem>().WithMany().HasForeignKey(x => x.RoadmapItemId).OnDelete(DeleteBehavior.SetNull);
        }
    }
}
