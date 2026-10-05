using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class ReportConfiguration : IEntityTypeConfiguration<Report>
    {
        public void Configure(EntityTypeBuilder<Report> builder)
        {
            builder.ToTable("reports", table =>
                table.HasCheckConstraint("ck_reports_target",
                    "(target_type = 'restaurant' AND restaurant_id IS NOT NULL AND blog_id IS NULL) OR " +
                    "(target_type = 'blog' AND blog_id IS NOT NULL AND restaurant_id IS NULL)"));

            builder.Property(x => x.TargetType).HasMaxLength(15);
            builder.Property(x => x.ReasonCode).HasMaxLength(30);
            builder.Property(x => x.Evidence).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            builder.Property(x => x.Status).HasMaxLength(15).HasDefaultValue(ReportStatus.Pending);
            builder.Property(x => x.ResolutionAction).HasMaxLength(20);

            builder.HasOne<User>().WithMany().HasForeignKey(x => x.ReporterId);
            builder.HasOne<Restaurant>().WithMany().HasForeignKey(x => x.RestaurantId);
            builder.HasOne<Blog>().WithMany().HasForeignKey(x => x.BlogId);
            builder.HasOne<User>().WithMany().HasForeignKey(x => x.HandledBy);
        }
    }
}
