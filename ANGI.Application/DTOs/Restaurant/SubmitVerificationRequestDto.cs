namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Contains the legal details and owned media submitted for OWN-06.</summary>
public sealed class SubmitVerificationRequestDto
{
    public string LegalName { get; set; } = string.Empty;
    public string BusinessLicenseNo { get; set; } = string.Empty;
    public string? TaxCode { get; set; }
    public string? OwnerNote { get; set; }
    public IReadOnlyList<VerificationDocumentRequestDto> Documents { get; set; } =
        Array.Empty<VerificationDocumentRequestDto>();
}
