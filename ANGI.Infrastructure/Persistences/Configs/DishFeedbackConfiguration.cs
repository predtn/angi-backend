using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class DishFeedbackConfiguration : IEntityTypeConfiguration<DishFeedback>
    {
        public void Configure(EntityTypeBuilder<DishFeedback> builder)
        {
            builder.ToTable("dish_feedbacks");

            builder.Property(x => x.EventUuid).HasDefaultValueSql("gen_random_uuid()");

            builder.HasIndex(x => x.EventUuid).IsUnique();
            builder.HasIndex(x => x.RoadmapItemId).IsUnique();

            builder.HasOne(x => x.Dish).WithMany().HasForeignKey(x => x.DishId);
            builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            // Data Dictionary: becomes NULL when the roadmap item is deleted
            builder.HasOne<RoadmapItem>().WithMany().HasForeignKey(x => x.RoadmapItemId).OnDelete(DeleteBehavior.SetNull);
        }
    }
}
