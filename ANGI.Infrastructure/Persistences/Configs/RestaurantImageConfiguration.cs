using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class RestaurantImageConfiguration : IEntityTypeConfiguration<RestaurantImage>
    {
        public void Configure(EntityTypeBuilder<RestaurantImage> builder)
        {
            builder.ToTable("restaurant_images");

            builder.HasKey(x => new { x.RestaurantId, x.MediaId });

            builder.Property(x => x.SortOrder).HasDefaultValue((short)0);

            builder.HasOne(x => x.Restaurant).WithMany(x => x.Images).HasForeignKey(x => x.RestaurantId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Media).WithMany().HasForeignKey(x => x.MediaId);
        }
    }
}
