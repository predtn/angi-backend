using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
    {
        public void Configure(EntityTypeBuilder<OutboxMessage> builder)
        {
            builder.ToTable("outbox_messages");

            builder.Property(x => x.EventUuid).HasDefaultValueSql("gen_random_uuid()");
            builder.Property(x => x.EventType).HasMaxLength(30);
            builder.Property(x => x.AggregateKey).HasMaxLength(60);
            builder.Property(x => x.Payload).HasColumnType("jsonb");
            builder.Property(x => x.Status).HasMaxLength(12).HasDefaultValue(OutboxStatus.Pending);
            builder.Property(x => x.AttemptCount).HasDefaultValue((short)0);
            builder.Property(x => x.NextAttemptAt).HasDefaultValueSql("now()");

            builder.HasIndex(x => x.EventUuid).IsUnique();
        }
    }
}
