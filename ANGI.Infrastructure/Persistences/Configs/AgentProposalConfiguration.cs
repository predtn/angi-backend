using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs;

/// <summary>Maps immutable snapshots and durable confirmation replay metadata.</summary>
public sealed class AgentProposalConfiguration : IEntityTypeConfiguration<AgentProposal>
{
    /// <summary>Enforces proposal revisions, finite source timestamps and complete committed results.</summary>
    public void Configure(EntityTypeBuilder<AgentProposal> builder)
    {
        builder.ToTable("agent_proposals", table =>
        {
            table.HasCheckConstraint("ck_agent_proposals_version", "proposal_version > 0");
            // PostgreSQL CHECK accepts UNKNOWN, so nullable timestamps need an explicit IS NOT NULL guard.
            table.HasCheckConstraint("ck_agent_proposals_base", "(operation = 'update' AND base_roadmap_id IS NOT NULL AND base_roadmap_updated_at IS NOT NULL AND isfinite(base_roadmap_updated_at)) OR (operation = 'create' AND base_roadmap_id IS NULL AND base_roadmap_updated_at IS NULL)");
            table.HasCheckConstraint("ck_agent_proposals_commit", "(status = 'committed' AND result_roadmap_id IS NOT NULL AND result_roadmap_updated_at IS NOT NULL AND isfinite(result_roadmap_updated_at) AND commit_result IS NOT NULL AND committed_at IS NOT NULL) OR (status <> 'committed' AND result_roadmap_id IS NULL AND result_roadmap_updated_at IS NULL AND commit_result IS NULL AND committed_at IS NULL)");
        });
        builder.Property(x => x.Operation).HasMaxLength(10);
        builder.Property(x => x.BaseRoadmapUpdatedAt).HasPrecision(6);
        builder.Property(x => x.ResultRoadmapUpdatedAt).HasPrecision(6);
        builder.Property(x => x.Status).HasMaxLength(16);
        builder.Property(x => x.Payload).HasColumnType("jsonb");
        builder.Property(x => x.ValidationResult).HasColumnType("jsonb");
        builder.Property(x => x.CommitResult).HasColumnType("jsonb");
        builder.Property(x => x.RecoRequestIds).HasDefaultValueSql("'{}'::uuid[]");
        builder.HasIndex(x => new { x.ConversationId, x.ProposalVersion }).IsUnique();
        builder.HasAlternateKey(x => new { x.ConversationId, x.Id });
        builder.HasOne<AgentConversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Roadmap>().WithMany().HasForeignKey(x => x.BaseRoadmapId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Roadmap>().WithMany().HasForeignKey(x => x.ResultRoadmapId).OnDelete(DeleteBehavior.Restrict);
    }
}
