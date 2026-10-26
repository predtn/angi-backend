using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs;

/// <summary>Maps ordered idempotent messages and protects retry metadata integrity.</summary>
public sealed class AgentMessageConfiguration : IEntityTypeConfiguration<AgentMessage>
{
    /// <summary>Defines partial retry uniqueness and user-only processing fields.</summary>
    public void Configure(EntityTypeBuilder<AgentMessage> builder)
    {
        builder.ToTable("agent_messages", table =>
        {
            table.HasCheckConstraint("ck_agent_messages_sequence", "sequence_no > 0");
            table.HasCheckConstraint("ck_agent_messages_ids", "turn_id > 0 AND (client_message_id IS NULL OR client_message_id > 0)");
            table.HasCheckConstraint("ck_agent_messages_retry", "(role = 'user' AND client_message_id IS NOT NULL AND processing_status IS NOT NULL AND request_hash IS NOT NULL) OR (role <> 'user' AND client_message_id IS NULL AND processing_status IS NULL AND request_hash IS NULL AND response_payload IS NULL)");
        });
        builder.Property(x => x.Role).HasMaxLength(16);
        builder.Property(x => x.ProcessingStatus).HasMaxLength(16);
        builder.Property(x => x.RequestHash).HasMaxLength(64);
        builder.Property(x => x.Payload).HasColumnType("jsonb");
        builder.Property(x => x.ResponsePayload).HasColumnType("jsonb");
        builder.HasIndex(x => new { x.ConversationId, x.SequenceNo }).IsUnique();
        builder.HasIndex(x => new { x.ConversationId, x.ClientMessageId }).IsUnique().HasFilter("client_message_id IS NOT NULL");
        builder.HasOne<AgentConversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}
