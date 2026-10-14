namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Identifies one owner-uploaded document in an OWN-06 request.</summary>
public sealed class VerificationDocumentRequestDto
{
    public string DocType { get; set; } = string.Empty;
    public long MediaId { get; set; }
}
