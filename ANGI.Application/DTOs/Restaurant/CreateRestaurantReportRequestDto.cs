namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Request body of REP-01 to report a restaurant.</summary>
public sealed class CreateRestaurantReportRequestDto
{
    /// <summary>wrong_info | closed | spam | offensive | hygiene | scam | other.</summary>
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>Optional description (≤ 1000 characters; required when reasonCode = other).</summary>
    public string? Description { get; set; }

    /// <summary>Optional list of media identifiers (≤ 5).</summary>
    public IReadOnlyList<long>? EvidenceMediaIds { get; set; }
}
