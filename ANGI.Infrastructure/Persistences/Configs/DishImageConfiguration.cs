using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class DishImageConfiguration : IEntityTypeConfiguration<DishImage>
    {
        public void Configure(EntityTypeBuilder<DishImage> builder)
        {
            builder.ToTable("dish_images");

            builder.HasKey(x => new { x.DishId, x.MediaId });

            builder.Property(x => x.SortOrder).HasDefaultValue((short)0);

            builder.HasOne(x => x.Dish).WithMany(x => x.Images).HasForeignKey(x => x.DishId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Media).WithMany().HasForeignKey(x => x.MediaId);
        }
    }
}
