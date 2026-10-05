using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class RoadmapItemConfiguration : IEntityTypeConfiguration<RoadmapItem>
    {
        public void Configure(EntityTypeBuilder<RoadmapItem> builder)
        {
            builder.ToTable("roadmap_items");

            builder.Property(x => x.MealSlot).HasMaxLength(10);
            builder.Property(x => x.Source).HasMaxLength(10);
            builder.Property(x => x.SortOrder).HasDefaultValue((short)0);

            builder.HasOne(x => x.RoadmapDay).WithMany(x => x.Items).HasForeignKey(x => x.RoadmapDayId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Dish).WithMany().HasForeignKey(x => x.DishId);
        }
    }
}
