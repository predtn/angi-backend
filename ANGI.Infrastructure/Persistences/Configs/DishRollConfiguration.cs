using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class DishRollConfiguration : IEntityTypeConfiguration<DishRoll>
    {
        public void Configure(EntityTypeBuilder<DishRoll> builder)
        {
            builder.ToTable("dish_rolls");

            builder.Property(x => x.RolledAt).HasDefaultValueSql("now()");

            builder.HasOne(x => x.Dish).WithMany().HasForeignKey(x => x.DishId);
            builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        }
    }
}
