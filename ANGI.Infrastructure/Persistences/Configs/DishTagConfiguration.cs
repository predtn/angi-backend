using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class DishTagConfiguration : IEntityTypeConfiguration<DishTag>
    {
        public void Configure(EntityTypeBuilder<DishTag> builder)
        {
            builder.ToTable("dish_tags");

            builder.HasKey(x => new { x.DishId, x.TagId });

            builder.HasOne(x => x.Dish).WithMany(x => x.DishTags).HasForeignKey(x => x.DishId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Tag).WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}
