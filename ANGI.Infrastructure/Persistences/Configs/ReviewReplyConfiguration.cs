using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class ReviewReplyConfiguration : IEntityTypeConfiguration<ReviewReply>
    {
        public void Configure(EntityTypeBuilder<ReviewReply> builder)
        {
            builder.ToTable("review_replies");

            builder.HasKey(x => x.ReviewId);

            builder.Property(x => x.Status).HasMaxLength(10).HasDefaultValue(ContentStatus.Visible);

            builder.HasOne(x => x.Review).WithOne(x => x.Reply).HasForeignKey<ReviewReply>(x => x.ReviewId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<User>().WithMany().HasForeignKey(x => x.ReplierId);
        }
    }
}
