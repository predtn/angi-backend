namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Represents one document attached to a restaurant verification submission.</summary>
public sealed class VerificationDocumentDto
{
    public string DocType { get; set; } = string.Empty;
    public long MediaId { get; set; }
    public string Url { get; set; } = string.Empty;
}
