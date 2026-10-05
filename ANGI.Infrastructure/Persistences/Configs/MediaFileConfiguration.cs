using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class MediaFileConfiguration : IEntityTypeConfiguration<MediaFile>
    {
        public void Configure(EntityTypeBuilder<MediaFile> builder)
        {
            builder.ToTable("media_files");

            builder.Property(x => x.MimeType).HasMaxLength(100);

            builder.HasIndex(x => x.StorageKey).IsUnique();

            builder.HasOne<User>().WithMany().HasForeignKey(x => x.UploaderId);
        }
    }
}
