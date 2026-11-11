using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class BlogImageConfiguration : IEntityTypeConfiguration<BlogImage>
    {
        public void Configure(EntityTypeBuilder<BlogImage> builder)
        {
            builder.ToTable("blog_images");

            builder.HasKey(x => new { x.BlogId, x.MediaId });

            builder.Property(x => x.SortOrder).HasDefaultValue((short)0);

            builder.HasOne(x => x.Blog).WithMany(x => x.Images).HasForeignKey(x => x.BlogId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Media).WithMany().HasForeignKey(x => x.MediaId);
        }
    }
}
