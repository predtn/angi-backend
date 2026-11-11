namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Identifies the restaurant's current draft or pending menu submission.</summary>
public sealed class OpenMenuSubmissionDto
{
    public long Id { get; set; }
    public string Status { get; set; } = string.Empty;
}
