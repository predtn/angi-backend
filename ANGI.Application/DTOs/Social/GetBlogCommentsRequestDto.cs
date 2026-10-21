namespace ANGI.Application.DTOs.Social;

/// <summary>Encapsulates pagination query parameters for BLOG-03 blog comments listing.</summary>
public sealed class GetBlogCommentsRequestDto
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
