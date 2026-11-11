using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class RoadmapDayConfiguration : IEntityTypeConfiguration<RoadmapDay>
    {
        public void Configure(EntityTypeBuilder<RoadmapDay> builder)
        {
            builder.ToTable("roadmap_days");

            builder.HasIndex(x => new { x.RoadmapId, x.DayNumber }).IsUnique();

            builder.HasOne(x => x.Roadmap).WithMany(x => x.Days).HasForeignKey(x => x.RoadmapId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}
