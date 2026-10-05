using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class UserConfiguration : IEntityTypeConfiguration<User>
    {
        public void Configure(EntityTypeBuilder<User> builder)
        {
            builder.ToTable("users");

            builder.Property(x => x.Email).HasColumnType("citext");
            builder.Property(x => x.DisplayName).HasMaxLength(100);
            builder.Property(x => x.Phone).HasMaxLength(20);
            builder.Property(x => x.Bio).HasMaxLength(500);
            builder.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(UserStatus.PendingVerification);

            builder.HasIndex(x => x.Email).IsUnique().HasFilter("deleted_at IS NULL");

            builder.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId);
            builder.HasOne(x => x.AvatarMedia).WithMany().HasForeignKey(x => x.AvatarMediaId);
        }
    }
}
