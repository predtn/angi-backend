using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class MenuSubmissionConfiguration : IEntityTypeConfiguration<MenuSubmission>
    {
        public void Configure(EntityTypeBuilder<MenuSubmission> builder)
        {
            builder.ToTable("menu_submissions");

            builder.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(MenuSubmissionStatus.Draft);

            // At most one draft or pending submission per restaurant
            builder.HasIndex(x => x.RestaurantId).IsUnique().HasFilter("status IN ('draft', 'pending')");

            builder.HasOne(x => x.Restaurant).WithMany().HasForeignKey(x => x.RestaurantId);
            builder.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy);
            builder.HasOne<User>().WithMany().HasForeignKey(x => x.ReviewedBy);
        }
    }
}
