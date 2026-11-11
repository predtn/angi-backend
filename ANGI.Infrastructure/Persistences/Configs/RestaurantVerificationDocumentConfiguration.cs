using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    /// <summary>Configures documents attached to restaurant verification submissions.</summary>
    public class RestaurantVerificationDocumentConfiguration : IEntityTypeConfiguration<RestaurantVerificationDocument>
    {
        /// <summary>Maps document keys and verification/media relationships.</summary>
        public void Configure(EntityTypeBuilder<RestaurantVerificationDocument> builder)
        {
            builder.ToTable("restaurant_verification_documents");

            builder.Property(x => x.DocType).HasMaxLength(30);

            builder.HasOne(x => x.Verification).WithMany(x => x.Documents).HasForeignKey(x => x.VerificationId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Media).WithMany().HasForeignKey(x => x.MediaId);
        }
    }
}
