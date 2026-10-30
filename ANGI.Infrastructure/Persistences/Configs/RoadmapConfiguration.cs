using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    /// <summary>Maps the roadmap aggregate, origin invariant and database-managed timestamp token.</summary>
    public class RoadmapConfiguration : IEntityTypeConfiguration<Roadmap>
    {
        /// <summary>Configures roadmap columns, business checks and owned-user references.</summary>
        public void Configure(EntityTypeBuilder<Roadmap> builder)
        {
            builder.ToTable("roadmaps", table =>
            {
                table.HasCheckConstraint("ck_roadmaps_num_days", "num_days BETWEEN 1 AND 30");
                table.HasCheckConstraint("ck_roadmaps_budget_amount", "budget_amount >= 0");
                table.HasCheckConstraint("ck_roadmaps_updated_at", "isfinite(updated_at)");
                // Data Dictionary 1.1: the coordinates are stored if and only if origin_mode = 'pinned'
                table.HasCheckConstraint("ck_roadmaps_origin",
                    "(origin_mode = 'pinned' AND origin_latitude IS NOT NULL AND origin_longitude IS NOT NULL) OR " +
                    "(origin_mode <> 'pinned' AND origin_latitude IS NULL AND origin_longitude IS NULL)");
            });

            builder.Property(x => x.Title).HasMaxLength(200);
            // PostgreSQL owns the exact microsecond token, including child mutations. Writers lock the parent
            // and compare snapshots explicitly; a concurrency predicate could conflict with our own child trigger.
            builder.Property(x => x.UpdatedAt).HasPrecision(6).ValueGeneratedOnAddOrUpdate();
            builder.Property(x => x.BudgetAmount).HasPrecision(14, 0);
            builder.Property(x => x.OriginMode).HasMaxLength(10).HasDefaultValue(RoadmapOriginMode.None);
            builder.Property(x => x.OriginLatitude).HasPrecision(9, 6);
            builder.Property(x => x.OriginLongitude).HasPrecision(9, 6);
            builder.Property(x => x.OriginLabel).HasMaxLength(200);

            builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        }
    }
}
