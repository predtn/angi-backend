using ANGI.Application.DTOs.Account;

namespace ANGI.Application.DTOs.Moderation;

/// <summary>Represents a restaurant or blog report with signed evidence and public user summaries.</summary>
public sealed class ReportDto
{
    public long Id { get; init; }
    public string TargetType { get; init; } = string.Empty;
    public long TargetId { get; init; }
    public string TargetName { get; init; } = string.Empty;
    public string ReasonCode { get; init; } = string.Empty;
    public string? Description { get; init; }
    public IReadOnlyList<string> EvidenceUrls { get; init; } = [];
    public UserSummaryDto Reporter { get; init; } = new();
    public string Status { get; init; } = string.Empty;
    public UserSummaryDto? HandledBy { get; init; }
    public DateTime? HandledAt { get; init; }
    public string? ResolutionAction { get; init; }
    public string? ResolutionNote { get; init; }
    public int ReportCountOnTarget { get; init; }
    public DateTime CreatedAt { get; init; }
}
