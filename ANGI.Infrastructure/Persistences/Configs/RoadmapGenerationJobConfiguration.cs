using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class RoadmapGenerationJobConfiguration : IEntityTypeConfiguration<RoadmapGenerationJob>
    {
        public void Configure(EntityTypeBuilder<RoadmapGenerationJob> builder)
        {
            builder.ToTable("roadmap_generation_jobs");

            builder.Property(x => x.Scope).HasMaxLength(10).HasDefaultValue(GenerationScope.Full);
            builder.Property(x => x.Input).HasColumnType("jsonb");
            builder.Property(x => x.ModelName).HasMaxLength(100);
            builder.Property(x => x.PromptVersion).HasMaxLength(20);
            builder.Property(x => x.RawOutput).HasColumnType("jsonb");
            builder.Property(x => x.Status).HasMaxLength(15).HasDefaultValue(GenerationJobStatus.Queued);

            builder.HasOne(x => x.Roadmap).WithMany().HasForeignKey(x => x.RoadmapId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<User>().WithMany().HasForeignKey(x => x.RequestedBy);
        }
    }
}
