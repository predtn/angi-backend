namespace ANGI.Application.DTOs.Social;

/// <summary>Request body of REP-02.</summary>
public sealed class ReportBlogRequestDto
{
    /// <summary>spam | offensive | wrong_info | scam | other.</summary>
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>Optional description (≤ 1000 characters; required when reasonCode = other).</summary>
    public string? Description { get; set; }

    /// <summary>Optional list of media identifiers (≤ 5).</summary>
    public IReadOnlyList<long>? EvidenceMediaIds { get; set; }
}
