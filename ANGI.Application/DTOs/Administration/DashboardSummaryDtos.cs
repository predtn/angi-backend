namespace ANGI.Application.DTOs.Administration;

/// <summary>Request parameters for retrieving the dashboard summary metrics.</summary>
public sealed class GetDashboardSummaryRequestDto
{
    private DateOnly? _from;
    private DateOnly? _to;

    /// <summary>The start of the reporting range (default: 30 days before today).</summary>
    public DateOnly? From { get => _from; set => _from = value; }

    /// <summary>The end of the reporting range (default: today).</summary>
    public DateOnly? To { get => _to; set => _to = value; }

    /// <summary>Alias for From (supports startDate query parameter).</summary>
    public DateOnly? StartDate { get => _from; set => _from ??= value; }

    /// <summary>Alias for To (supports endDate query parameter).</summary>
    public DateOnly? EndDate { get => _to; set => _to ??= value; }
}

/// <summary>Overall administrative dashboard summary metrics.</summary>
public sealed class DashboardSummaryDto
{
    /// <summary>The start of the reporting range.</summary>
    public DateOnly From { get; set; }

    /// <summary>The end of the reporting range.</summary>
    public DateOnly To { get; set; }

    /// <summary>User account metrics.</summary>
    public DashboardUsersSummaryDto Users { get; set; } = new();

    /// <summary>Restaurant metrics.</summary>
    public DashboardRestaurantsSummaryDto Restaurants { get; set; } = new();

    /// <summary>Moderator queue metrics.</summary>
    public DashboardQueuesSummaryDto Queues { get; set; } = new();

    /// <summary>Content generated within the period.</summary>
    public DashboardContentSummaryDto Content { get; set; } = new();

    /// <summary>Roadmap metrics within the period.</summary>
    public DashboardRoadmapsSummaryDto Roadmaps { get; set; } = new();

    /// <summary>Dish feedback counts within the period.</summary>
    public DashboardFeedbackSummaryDto Feedback { get; set; } = new();

    /// <summary>Outbox message synchronization status.</summary>
    public DashboardOutboxSummaryDto Outbox { get; set; } = new();
}

/// <summary>Account summary metrics.</summary>
public sealed class DashboardUsersSummaryDto
{
    /// <summary>Total non-deleted accounts.</summary>
    public int Total { get; set; }

    /// <summary>New accounts created within the period.</summary>
    public int New { get; set; }

    /// <summary>Account distribution by role (e.g. TRAVELER, RESTAURANT_OWNER, MOD, ADMIN).</summary>
    public Dictionary<string, int> ByRole { get; set; } = new();

    /// <summary>Account distribution by status (e.g. active, pending_verification, suspended, banned, deactivated).</summary>
    public Dictionary<string, int> ByStatus { get; set; } = new();
}

/// <summary>Restaurant summary metrics.</summary>
public sealed class DashboardRestaurantsSummaryDto
{
    /// <summary>Total non-deleted restaurants.</summary>
    public int Total { get; set; }

    /// <summary>New restaurants registered within the period.</summary>
    public int New { get; set; }

    /// <summary>Restaurant count by verification status (unverified, pending, verified, rejected).</summary>
    public Dictionary<string, int> ByVerificationStatus { get; set; } = new();

    /// <summary>Restaurant count by moderation status (visible, hidden, suspended).</summary>
    public Dictionary<string, int> ByModerationStatus { get; set; } = new();
}

/// <summary>Pending moderation tasks.</summary>
public sealed class DashboardQueuesSummaryDto
{
    /// <summary>Number of pending restaurant verification applications.</summary>
    public int PendingVerifications { get; set; }

    /// <summary>Number of pending menu submissions.</summary>
    public int PendingMenus { get; set; }

    /// <summary>Number of pending restaurant reports.</summary>
    public int PendingRestaurantReports { get; set; }

    /// <summary>Number of pending social/blog reports.</summary>
    public int PendingSocialReports { get; set; }
}

/// <summary>Content generation metrics within the period.</summary>
public sealed class DashboardContentSummaryDto
{
    /// <summary>Number of blogs published within the period.</summary>
    public int Blogs { get; set; }

    /// <summary>Number of comments created within the period.</summary>
    public int Comments { get; set; }

    /// <summary>Number of reviews posted within the period.</summary>
    public int Reviews { get; set; }
}

/// <summary>Roadmap metrics within the period.</summary>
public sealed class DashboardRoadmapsSummaryDto
{
    /// <summary>Number of roadmaps created within the period.</summary>
    public int Created { get; set; }

    /// <summary>Number of AI roadmap generation jobs within the period.</summary>
    public int AiGenerationJobs { get; set; }
}

/// <summary>Feedback counts within the period.</summary>
public sealed class DashboardFeedbackSummaryDto
{
    /// <summary>Total likes on dishes.</summary>
    public int Likes { get; set; }

    /// <summary>Total dislikes on dishes.</summary>
    public int Dislikes { get; set; }
}

/// <summary>Outbox message synchronization status.</summary>
public sealed class DashboardOutboxSummaryDto
{
    /// <summary>Number of pending outbox messages.</summary>
    public int Pending { get; set; }

    /// <summary>Number of failed (dead) outbox messages.</summary>
    public int Dead { get; set; }
}
