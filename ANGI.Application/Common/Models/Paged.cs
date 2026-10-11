namespace ANGI.Application.Common.Models;

/// <summary>Represents a page of API results together with pagination metadata.</summary>
public sealed class Paged<T>
{
    public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public long TotalItems { get; set; }
    public int TotalPages { get; set; }
}
