namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Response body of REP-01.</summary>
public sealed class CreateRestaurantReportResponseDto
{
    /// <summary>Created report identifier.</summary>
    public long ReportId { get; set; }

    /// <summary>Report status (pending).</summary>
    public string Status { get; set; } = "pending";
}
