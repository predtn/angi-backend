using ANGI.Application.DTOs.Account;

namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Represents a restaurant verification submission in owner-facing responses.</summary>
public sealed class VerificationDto
{
    public long Id { get; set; }
    public long RestaurantId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string BusinessLicenseNo { get; set; } = string.Empty;
    public string? TaxCode { get; set; }
    public string? OwnerNote { get; set; }
    public IReadOnlyList<VerificationDocumentDto> Documents { get; set; } = Array.Empty<VerificationDocumentDto>();
    public long? PreviousId { get; set; }
    public DateTime SubmittedAt { get; set; }
    public UserSummaryDto? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
}
