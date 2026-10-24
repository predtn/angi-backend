namespace ANGI.Application.DTOs.Social;

/// <summary>Encapsulates pagination and status filter query parameters for BLOG-05.</summary>
public sealed class GetMyBlogsRequestDto
{
    private readonly int _page = 1;

    /// <summary>Optional status filter: published, hidden, removed.</summary>
    public string? Status { get; init; }

    /// <summary>Current page index, starting from 1 (default: 1).</summary>
    public int Page
    {
        get => PageIndex ?? _page;
        init => _page = value;
    }

    /// <summary>Alternative alias for page parameter.</summary>
    public int? PageIndex { get; init; }

    /// <summary>Number of items per page (default: 20, max: 100).</summary>
    public int PageSize { get; init; } = 20;
}
