using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class UserSanctionConfiguration : IEntityTypeConfiguration<UserSanction>
    {
        public void Configure(EntityTypeBuilder<UserSanction> builder)
        {
            builder.ToTable("user_sanctions");

            builder.Property(x => x.Type).HasMaxLength(10);
            builder.Property(x => x.StartsAt).HasDefaultValueSql("now()");

            builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            builder.HasOne<Report>().WithMany().HasForeignKey(x => x.ReportId);
            builder.HasOne<User>().WithMany().HasForeignKey(x => x.IssuedBy);
            builder.HasOne<User>().WithMany().HasForeignKey(x => x.LiftedBy);
        }
    }
}
