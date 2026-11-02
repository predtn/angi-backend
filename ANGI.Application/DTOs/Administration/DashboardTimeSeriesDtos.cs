namespace ANGI.Application.DTOs.Administration;

/// <summary>Request parameters for querying time-series dashboard data.</summary>
public sealed class GetDashboardTimeSeriesRequestDto
{
    private DateOnly? _from;
    private DateOnly? _to;

    /// <summary>The metric to aggregate (new_users, new_restaurants, roadmaps, blogs, reports, feedbacks, dish_rolls).</summary>
    public string Metric { get; set; } = string.Empty;

    /// <summary>The start of the reporting range.</summary>
    public DateOnly? From { get => _from; set => _from = value; }

    /// <summary>The end of the reporting range.</summary>
    public DateOnly? To { get => _to; set => _to = value; }

    /// <summary>Alias for From (supports startDate query parameter).</summary>
    public DateOnly? StartDate { get => _from; set => _from ??= value; }

    /// <summary>Alias for To (supports endDate query parameter).</summary>
    public DateOnly? EndDate { get => _to; set => _to ??= value; }

    /// <summary>Aggregation interval (day, week, month; defaults to day).</summary>
    public string? Interval { get; set; }
}

/// <summary>Represents time-series chart data.</summary>
public sealed class DashboardTimeSeriesDto
{
    /// <summary>The aggregated metric name.</summary>
    public string Metric { get; set; } = string.Empty;

    /// <summary>The aggregation interval (day, week, month).</summary>
    public string Interval { get; set; } = string.Empty;

    /// <summary>The data points in chronological order.</summary>
    public IReadOnlyList<DashboardTimeSeriesPointDto> Points { get; set; } = Array.Empty<DashboardTimeSeriesPointDto>();
}

/// <summary>A single bucket in the time series.</summary>
public sealed class DashboardTimeSeriesPointDto
{
    /// <summary>The start date of the bucket.</summary>
    public DateOnly Bucket { get; set; }

    /// <summary>The aggregated count for this bucket.</summary>
    public int Value { get; set; }
}
