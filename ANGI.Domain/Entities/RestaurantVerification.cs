using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class RestaurantVerification : BaseEntity<long>
    {
        public long RestaurantId { get; set; }
        public int SubmittedBy { get; set; }
        public long? PreviousId { get; set; }
        public string? LegalName { get; set; }
        public string? BusinessLicenseNo { get; set; }
        public string? TaxCode { get; set; }
        public string? OwnerNote { get; set; }
        public VerificationStatus Status { get; set; } = VerificationStatus.Pending;
        public int? ReviewedBy { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? ReviewNote { get; set; }
        public DateTime SubmittedAt { get; set; }

        public Restaurant Restaurant { get; set; } = null!;
        public RestaurantVerification? Previous { get; set; }
        public ICollection<RestaurantVerificationDocument> Documents { get; set; } = new List<RestaurantVerificationDocument>();
    }
}
