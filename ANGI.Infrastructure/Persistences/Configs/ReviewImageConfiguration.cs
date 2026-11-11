using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class ReviewImageConfiguration : IEntityTypeConfiguration<ReviewImage>
    {
        public void Configure(EntityTypeBuilder<ReviewImage> builder)
        {
            builder.ToTable("review_images");

            builder.HasKey(x => new { x.ReviewId, x.MediaId });

            builder.HasOne(x => x.Review).WithMany(x => x.Images).HasForeignKey(x => x.ReviewId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Media).WithMany().HasForeignKey(x => x.MediaId);
        }
    }
}
