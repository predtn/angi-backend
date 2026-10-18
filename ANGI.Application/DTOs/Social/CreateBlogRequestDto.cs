namespace ANGI.Application.DTOs.Social;

/// <summary>Request body accepted by BLOG-04.</summary>
public sealed class CreateBlogRequestDto
{
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public long? CoverMediaId { get; set; }
    public IReadOnlyList<long>? ImageMediaIds { get; set; }
    public long? RoadmapId { get; set; }
}
