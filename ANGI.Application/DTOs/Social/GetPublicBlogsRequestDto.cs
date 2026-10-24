namespace ANGI.Application.DTOs.Social;

/// <summary>Encapsulates query parameters for BLOG-01 public blog listing.</summary>
public sealed class GetPublicBlogsRequestDto
{
    public string? Q { get; init; }
    public int? AuthorId { get; init; }
    public bool? HasRoadmap { get; init; }
    public string? Sort { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
