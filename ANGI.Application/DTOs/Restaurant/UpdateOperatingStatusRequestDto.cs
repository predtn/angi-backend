namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Request body of OWN-05.</summary>
public sealed class UpdateOperatingStatusRequestDto
{
    /// <summary>open | temporarily_closed | permanently_closed.</summary>
    public string? OperatingStatus { get; set; }

    /// <summary>Optional reason (≤ 500), written to the audit log.</summary>
    public string? Note { get; set; }
}
