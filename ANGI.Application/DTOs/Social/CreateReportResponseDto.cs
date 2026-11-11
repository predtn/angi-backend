namespace ANGI.Application.DTOs.Social;

/// <summary>Response body of REP-02.</summary>
public sealed class CreateReportResponseDto
{
    /// <summary>Created report identifier.</summary>
    public long ReportId { get; set; }

    /// <summary>Report status (pending).</summary>
    public string Status { get; set; } = "pending";
}
