namespace ANGI.Application.DTOs.Moderation;

/// <summary>Query parameters for listing blogs in moderation (MOD-B1).</summary>
public sealed class GetModBlogsRequestDto
{
    /// <summary>Optional search term for title or content.</summary>
    public string? Q { get; init; }

    /// <summary>Optional status filter: published | hidden | removed.</summary>
    public string? Status { get; init; }

    /// <summary>Optional author identifier filter.</summary>
    public int? AuthorId { get; init; }

    /// <summary>Optional filter for blogs with pending reports.</summary>
    public bool? Reported { get; init; }

    /// <summary>Alias for Reported filter (hasReports).</summary>
    public bool? HasReports { get; init; }

    /// <summary>1-based page number, defaults to 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Items per page, defaults to 20.</summary>
    public int PageSize { get; init; } = 20;

    /// <summary>Gets effective pending reports filter value.</summary>
    public bool? HasPendingReportsFilter => Reported ?? HasReports;
}
