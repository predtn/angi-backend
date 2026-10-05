using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class DishConfiguration : IEntityTypeConfiguration<Dish>
    {
        public void Configure(EntityTypeBuilder<Dish> builder)
        {
            builder.ToTable("dishes", table =>
                table.HasCheckConstraint("ck_dishes_price", "price >= 0"));

            builder.Property(x => x.Name).HasMaxLength(200);
            builder.Property(x => x.DishForm).HasMaxLength(10).HasDefaultValue(DishForm.Other);
            builder.Property(x => x.LikeCount).HasDefaultValue(0);
            builder.Property(x => x.DislikeCount).HasDefaultValue(0);
            builder.Property(x => x.Price).HasPrecision(12, 0);
            builder.Property(x => x.IsAvailable).HasDefaultValue(true);
            builder.Property(x => x.Status).HasMaxLength(10).HasDefaultValue(DishStatus.Active);
            builder.Property(x => x.SortOrder).HasDefaultValue((short)0);

            builder.HasOne(x => x.Restaurant).WithMany(x => x.Dishes).HasForeignKey(x => x.RestaurantId);
            builder.HasOne(x => x.CoverMedia).WithMany().HasForeignKey(x => x.CoverMediaId);
        }
    }
}
