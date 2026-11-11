using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    /// <summary>Configures append-only audit records written by restaurant profile updates.</summary>
    public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
    {
        /// <summary>Maps audit payloads, actor metadata, and searchable indexes.</summary>
        public void Configure(EntityTypeBuilder<AuditLog> builder)
        {
            builder.ToTable("audit_logs");

            builder.Property(x => x.ActorRole).HasMaxLength(30);
            builder.Property(x => x.Action).HasMaxLength(60);
            builder.Property(x => x.EntityType).HasMaxLength(40);
            builder.Property(x => x.OldValues).HasColumnType("jsonb");
            builder.Property(x => x.NewValues).HasColumnType("jsonb");
            builder.Property(x => x.UserAgent).HasMaxLength(500);

            builder.HasOne(x => x.Actor).WithMany().HasForeignKey(x => x.ActorId);
            builder.HasOne(x => x.SubjectUser).WithMany().HasForeignKey(x => x.SubjectUserId);

            // ADM-A1 / MOD-U5 always sort by created_at DESC; each filter column is paired with it.
            builder.HasIndex(x => x.CreatedAt).IsDescending().HasDatabaseName("ix_audit_logs_created_at");
            builder.HasIndex(x => new { x.ActorId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_audit_logs_actor_id_created_at");
            builder.HasIndex(x => new { x.SubjectUserId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_audit_logs_subject_user_id_created_at");
            builder.HasIndex(x => new { x.Action, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_audit_logs_action_created_at");
            builder.HasIndex(x => new { x.EntityType, x.EntityId }).HasDatabaseName("ix_audit_logs_entity_type_entity_id");
        }
    }
}
