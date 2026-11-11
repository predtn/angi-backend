using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class BlogConfiguration : IEntityTypeConfiguration<Blog>
    {
        public void Configure(EntityTypeBuilder<Blog> builder)
        {
            builder.ToTable("blogs");

            builder.Property(x => x.RoadmapSnapshot).HasColumnType("jsonb");
            builder.Property(x => x.Title).HasMaxLength(255);
            builder.Property(x => x.Slug).HasMaxLength(280);
            builder.Property(x => x.Status).HasMaxLength(15).HasDefaultValue(BlogStatus.Published);
            builder.Property(x => x.PublishedAt).HasDefaultValueSql("now()");
            builder.Property(x => x.LikeCount).HasDefaultValue(0);
            builder.Property(x => x.CommentCount).HasDefaultValue(0);
            builder.Property(x => x.ViewCount).HasDefaultValue(0);

            builder.HasIndex(x => x.Slug).IsUnique().HasFilter("deleted_at IS NULL");

            builder.HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorId);
            builder.HasOne<Roadmap>().WithMany().HasForeignKey(x => x.RoadmapId);
            builder.HasOne(x => x.CoverMedia).WithMany().HasForeignKey(x => x.CoverMediaId);
        }
    }
}
