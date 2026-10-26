using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    /// <summary>Maps the roadmap aggregate, origin invariant and database-managed concurrency revision.</summary>
    public class RoadmapConfiguration : IEntityTypeConfiguration<Roadmap>
    {
        /// <summary>Configures roadmap columns, business checks and owned-user references.</summary>
        public void Configure(EntityTypeBuilder<Roadmap> builder)
        {
            builder.ToTable("roadmaps", table =>
            {
                table.HasCheckConstraint("ck_roadmaps_num_days", "num_days BETWEEN 1 AND 30");
                table.HasCheckConstraint("ck_roadmaps_budget_amount", "budget_amount >= 0");
                table.HasCheckConstraint("ck_roadmaps_version", "version > 0");
                // Data Dictionary 1.1: the coordinates are stored if and only if origin_mode = 'pinned'
                table.HasCheckConstraint("ck_roadmaps_origin",
                    "(origin_mode = 'pinned' AND origin_latitude IS NOT NULL AND origin_longitude IS NOT NULL) OR " +
                    "(origin_mode <> 'pinned' AND origin_latitude IS NULL AND origin_longitude IS NULL)");
            });

            builder.Property(x => x.Title).HasMaxLength(200);
            // The database advances the revision once per aggregate and transaction, including child writes.
            // Writers lock the parent before child mutations. An EF version predicate would conflict with a child
            // trigger advancing this same aggregate earlier in the current SaveChanges batch (e.g. shrinking days).
            builder.Property(x => x.Version).HasDefaultValueSql("1").ValueGeneratedOnAddOrUpdate();
            builder.Property(x => x.BudgetAmount).HasPrecision(14, 0);
            builder.Property(x => x.OriginMode).HasMaxLength(10).HasDefaultValue(RoadmapOriginMode.None);
            builder.Property(x => x.OriginLatitude).HasPrecision(9, 6);
            builder.Property(x => x.OriginLongitude).HasPrecision(9, 6);
            builder.Property(x => x.OriginLabel).HasMaxLength(200);

            builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        }
    }
}
