using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class UserExternalLoginConfiguration : IEntityTypeConfiguration<UserExternalLogin>
    {
        public void Configure(EntityTypeBuilder<UserExternalLogin> builder)
        {
            builder.ToTable("user_external_logins");

            builder.Property(x => x.Provider).HasMaxLength(20);
            builder.Property(x => x.ProviderUserId).HasMaxLength(255);
            builder.Property(x => x.LinkedAt).HasDefaultValueSql("now()");

            builder.HasIndex(x => new { x.UserId, x.Provider, x.ProviderUserId }).IsUnique();

            builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}
