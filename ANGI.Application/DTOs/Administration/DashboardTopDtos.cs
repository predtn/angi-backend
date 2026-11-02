namespace ANGI.Application.DTOs.Administration;

/// <summary>Request parameters for querying top ranking entities on the dashboard.</summary>
public sealed class GetDashboardTopRequestDto
{
    /// <summary>The entity category (dishes, restaurants, blogs).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Maximum number of items to return (1..50; defaults to 10).</summary>
    public int? Limit { get; set; }
}

/// <summary>Represents top ranking entities on the dashboard.</summary>
public sealed class DashboardTopDto
{
    /// <summary>The entity category (dishes, restaurants, blogs).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>List of top ranking items sorted descending by score.</summary>
    public IReadOnlyList<DashboardTopItemDto> Items { get; set; } = Array.Empty<DashboardTopItemDto>();
}

/// <summary>A single ranked entity item.</summary>
public sealed class DashboardTopItemDto
{
    /// <summary>The primary identifier of the entity.</summary>
    public long Id { get; set; }

    /// <summary>The display name / title of the entity.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The ranking score (like_count, rating_avg).</summary>
    public decimal Score { get; set; }

    /// <summary>Optional descriptive subtitle (e.g. restaurant name for dishes, author for blogs).</summary>
    public string? Subtitle { get; set; }
}
