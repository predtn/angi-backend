namespace ANGI.Application.Common.Models;

/// <summary>Represents a paginated subset of records along with pagination metadata.</summary>
public sealed class Paged<T>
{
    /// <summary>The collection of items on the current page.</summary>
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    /// <summary>The current page number (1-based).</summary>
    public int Page { get; init; }

    /// <summary>The number of items per page.</summary>
    public int PageSize { get; init; }

    /// <summary>The total number of items matching the query criteria.</summary>
    public long TotalItems { get; init; }

    /// <summary>The total number of pages available.</summary>
    public int TotalPages { get; init; }
}
