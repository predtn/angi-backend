namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Contains the complete replacement dossier submitted for OWN-08.</summary>
/// <remarks>
/// Required fields are nullable so an explicit null reaches the validator, which reports it in Vietnamese, instead of
/// MVC's implicit [Required] for non-nullable references.
/// </remarks>
public sealed class ResubmitVerificationRequestDto
{
    public string? LegalName { get; set; }
    public string? BusinessLicenseNo { get; set; }
    public string? TaxCode { get; set; }
    public string? OwnerNote { get; set; }
    public IReadOnlyList<VerificationDocumentRequestDto>? Documents { get; set; }
}
