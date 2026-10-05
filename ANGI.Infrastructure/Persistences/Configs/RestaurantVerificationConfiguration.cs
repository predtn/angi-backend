using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class RestaurantVerificationConfiguration : IEntityTypeConfiguration<RestaurantVerification>
    {
        public void Configure(EntityTypeBuilder<RestaurantVerification> builder)
        {
            builder.ToTable("restaurant_verifications");

            builder.Property(x => x.LegalName).HasMaxLength(255);
            builder.Property(x => x.BusinessLicenseNo).HasMaxLength(50);
            builder.Property(x => x.TaxCode).HasMaxLength(20);
            builder.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(VerificationStatus.Pending);
            builder.Property(x => x.SubmittedAt).HasDefaultValueSql("now()");

            builder.HasOne(x => x.Restaurant).WithMany(x => x.Verifications).HasForeignKey(x => x.RestaurantId);
            builder.HasOne(x => x.Previous).WithMany().HasForeignKey(x => x.PreviousId);
            builder.HasOne<User>().WithMany().HasForeignKey(x => x.SubmittedBy);
            builder.HasOne<User>().WithMany().HasForeignKey(x => x.ReviewedBy);
        }
    }
}
