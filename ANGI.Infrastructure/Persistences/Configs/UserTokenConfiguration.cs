using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    /// <summary>Configures persistence for one-time account verification and password-reset tokens.</summary>
    public class UserTokenConfiguration : IEntityTypeConfiguration<UserToken>
    {
        /// <summary>Maps user token constraints, enum storage, uniqueness, and ownership.</summary>
        public void Configure(EntityTypeBuilder<UserToken> builder)
        {
            builder.ToTable("user_tokens");

            builder.Property(x => x.Purpose).HasMaxLength(20);

            builder.HasIndex(x => x.TokenHash).IsUnique();

            builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}
