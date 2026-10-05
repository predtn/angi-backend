using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class MenuSubmissionItemConfiguration : IEntityTypeConfiguration<MenuSubmissionItem>
    {
        public void Configure(EntityTypeBuilder<MenuSubmissionItem> builder)
        {
            builder.ToTable("menu_submission_items", table =>
                table.HasCheckConstraint("ck_menu_submission_items_price", "price >= 0"));

            builder.Property(x => x.Name).HasMaxLength(200);
            builder.Property(x => x.DishForm).HasMaxLength(10).HasDefaultValue(DishForm.Other);
            builder.Property(x => x.Price).HasPrecision(12, 0);
            builder.Property(x => x.TagIds).HasDefaultValueSql("'{}'::smallint[]");
            builder.Property(x => x.SortOrder).HasDefaultValue((short)0);

            builder.HasIndex(x => new { x.SubmissionId, x.SourceDishId }).IsUnique();

            builder.HasOne(x => x.Submission).WithMany(x => x.Items).HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<Dish>().WithMany().HasForeignKey(x => x.SourceDishId);
            builder.HasOne<Dish>().WithMany().HasForeignKey(x => x.ResultDishId);
            builder.HasOne<MediaFile>().WithMany().HasForeignKey(x => x.CoverMediaId);
        }
    }
}
