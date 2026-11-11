namespace ANGI.Application.DTOs.Moderation;

/// <summary>Contains a report conclusion and the action to apply atomically with it.</summary>
public sealed class ResolveReportRequestDto
{
    public string Outcome { get; set; } = string.Empty;
    public string ResolutionAction { get; set; } = string.Empty;
    public string? ResolutionNote { get; set; }
    public DateTimeOffset? SuspendUntil { get; set; }
    public bool ApplyToRelatedReports { get; set; } = true;
}
