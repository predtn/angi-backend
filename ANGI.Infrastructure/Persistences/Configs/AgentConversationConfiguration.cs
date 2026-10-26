using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs;

/// <summary>Maps expiring conversation state and enforces lease and current-proposal ownership.</summary>
public sealed class AgentConversationConfiguration : IEntityTypeConfiguration<AgentConversation>
{
    /// <summary>Configures JSON, keys, positive versions and an all-or-none processing lease.</summary>
    public void Configure(EntityTypeBuilder<AgentConversation> builder)
    {
        builder.ToTable("agent_conversations", table =>
        {
            table.HasCheckConstraint("ck_agent_conversations_processing_ids", "(processing_turn_id IS NULL OR processing_turn_id > 0) AND (processing_attempt_id IS NULL OR processing_attempt_id > 0)");
            table.HasCheckConstraint("ck_agent_conversations_version", "version > 0");
            table.HasCheckConstraint("ck_agent_conversations_lease", "(processing_turn_id IS NULL AND processing_attempt_id IS NULL AND processing_until IS NULL) OR (processing_turn_id IS NOT NULL AND processing_attempt_id IS NOT NULL AND processing_until IS NOT NULL)");
        });
        builder.Property(x => x.Status).HasMaxLength(16);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn().ValueGeneratedOnAdd();
        builder.Property(x => x.State).HasColumnType("jsonb");
        builder.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
        builder.HasIndex(x => new { x.UserId, x.UpdatedAt }).IsDescending(false, true);
        builder.HasIndex(x => x.ExpiresAt);
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Roadmap>().WithMany().HasForeignKey(x => x.RoadmapId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.CurrentProposal).WithMany()
            .HasForeignKey(x => new { x.Id, x.CurrentProposalId })
            .HasPrincipalKey(x => new { x.ConversationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
