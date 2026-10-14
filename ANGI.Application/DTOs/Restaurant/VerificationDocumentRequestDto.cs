namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Identifies one owner-uploaded document in an OWN-06 request.</summary>
/// <remarks>
/// Required fields are nullable so an explicit null reaches the validator, which reports it in Vietnamese, instead of
/// MVC's implicit [Required] for non-nullable references.
/// </remarks>
public sealed class VerificationDocumentRequestDto
{
    public string? DocType { get; set; }
    public long MediaId { get; set; }
}
