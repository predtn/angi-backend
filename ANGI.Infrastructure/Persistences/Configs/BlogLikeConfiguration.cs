using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class BlogLikeConfiguration : IEntityTypeConfiguration<BlogLike>
    {
        public void Configure(EntityTypeBuilder<BlogLike> builder)
        {
            builder.ToTable("blog_likes");

            builder.HasKey(x => new { x.BlogId, x.UserId });

            builder.HasOne(x => x.Blog).WithMany(x => x.Likes).HasForeignKey(x => x.BlogId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        }
    }
}
