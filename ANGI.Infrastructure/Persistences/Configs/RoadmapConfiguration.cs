using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class RoadmapConfiguration : IEntityTypeConfiguration<Roadmap>
    {
        public void Configure(EntityTypeBuilder<Roadmap> builder)
        {
            builder.ToTable("roadmaps", table =>
            {
                table.HasCheckConstraint("ck_roadmaps_num_days", "num_days BETWEEN 1 AND 30");
                table.HasCheckConstraint("ck_roadmaps_budget_amount", "budget_amount >= 0");
            });

            builder.Property(x => x.Title).HasMaxLength(200);
            builder.Property(x => x.BudgetAmount).HasPrecision(14, 0);

            builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        }
    }
}
