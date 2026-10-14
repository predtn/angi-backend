using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    /// <summary>Configures the dish tag catalog used by menu submissions.</summary>
    public class TagConfiguration : IEntityTypeConfiguration<Tag>
    {
        /// <summary>Maps tag fields, uniqueness, and defaults.</summary>
        public void Configure(EntityTypeBuilder<Tag> builder)
        {
            builder.ToTable("tags");

            builder.Property(x => x.TagType).HasMaxLength(20);
            builder.Property(x => x.Code).HasMaxLength(50);
            builder.Property(x => x.Name).HasMaxLength(100);

            builder.HasIndex(x => new { x.TagType, x.Code }).IsUnique();
        }
    }
}
