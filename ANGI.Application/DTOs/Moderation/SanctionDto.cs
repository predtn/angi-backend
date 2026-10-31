using ANGI.Application.DTOs.Account;

namespace ANGI.Application.DTOs.Moderation;

/// <summary>Represents a user sanction entry (warn, suspend, ban) with moderator audit information.</summary>
public sealed class SanctionDto
{
    public long Id { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public long? ReportId { get; init; }
    public UserSummaryDto IssuedBy { get; init; } = new();
    public DateTime StartsAt { get; init; }
    public DateTime? EndsAt { get; init; }
    public DateTime? LiftedAt { get; init; }
    public UserSummaryDto? LiftedBy { get; init; }
    public string? LiftReason { get; init; }
}
