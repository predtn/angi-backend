namespace ANGI.Application.DTOs.Moderation;

/// <summary>Request payload for MOD-U3: applying a sanction (warn, suspend, ban) against an account.</summary>
public sealed class CreateUserSanctionRequestDto
{
    /// <summary>Sanction type: warn, suspend, ban.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Optional alias for Type to support either naming convention.</summary>
    public string? SanctionType
    {
        get => Type;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                Type = value;
        }
    }

    /// <summary>Moderator's reason for the sanction (1..1000 characters).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Expiration datetime for suspension in UTC (required when type is suspend and durationDays is not provided).</summary>
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>Optional duration in days for suspension (alternative to endsAt).</summary>
    public int? DurationDays { get; set; }

    /// <summary>Optional identifier of the report that led to this sanction.</summary>
    public long? ReportId { get; set; }
}
