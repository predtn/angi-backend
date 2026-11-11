namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Contains the legal details and owned media submitted for OWN-06.</summary>
/// <remarks>
/// Required fields are nullable so an explicit null reaches the validator, which reports it in Vietnamese, instead of
/// MVC's implicit [Required] for non-nullable references.
/// </remarks>
public sealed class SubmitVerificationRequestDto
{
    public string? LegalName { get; set; }
    public string? BusinessLicenseNo { get; set; }
    public string? TaxCode { get; set; }
    public string? OwnerNote { get; set; }
    public IReadOnlyList<VerificationDocumentRequestDto>? Documents { get; set; }
}
