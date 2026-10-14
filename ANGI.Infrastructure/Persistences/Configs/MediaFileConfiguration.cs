using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    /// <summary>Configures uploaded media referenced by restaurant profiles and menu items.</summary>
    public class MediaFileConfiguration : IEntityTypeConfiguration<MediaFile>
    {
        /// <summary>Maps uploaded file metadata and its uploader relationship.</summary>
        public void Configure(EntityTypeBuilder<MediaFile> builder)
        {
            builder.ToTable("media_files");

            builder.Property(x => x.MimeType).HasMaxLength(100);

            builder.HasIndex(x => x.StorageKey).IsUnique();

            builder.HasOne<User>().WithMany().HasForeignKey(x => x.UploaderId);
        }
    }
}
