using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class RestaurantVerificationDocument : BaseEntity<long>
    {
        public long VerificationId { get; set; }
        public VerificationDocumentType DocType { get; set; }
        public long MediaId { get; set; }

        public RestaurantVerification Verification { get; set; } = null!;
        public MediaFile Media { get; set; } = null!;
    }
}
