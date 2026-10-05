using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
    {
        public void Configure(EntityTypeBuilder<Notification> builder)
        {
            builder.ToTable("notifications");

            builder.Property(x => x.Type).HasMaxLength(40);
            builder.Property(x => x.Title).HasMaxLength(255);
            builder.Property(x => x.Data).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");

            builder.HasOne<User>().WithMany().HasForeignKey(x => x.RecipientId);
        }
    }
}
