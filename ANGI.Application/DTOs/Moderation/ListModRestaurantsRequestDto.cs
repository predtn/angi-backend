namespace ANGI.Application.DTOs.Moderation;

/// <summary>Contains the documented MOD-R1 filters and pagination.</summary>
public sealed class ListModRestaurantsRequestDto
{
    public string? Q { get; set; }
    public string? Province { get; set; }
    public string? VerificationStatus { get; set; }
    public string? OperatingStatus { get; set; }
    public string? ModerationStatus { get; set; }
    public bool? HasPendingReports { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
