using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
    {
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
        }
    }
}
