namespace ANGI.Application.DTOs.Social;

/// <summary>Request payload for creating a blog comment (BLOG-10).</summary>
public sealed class CreateBlogCommentRequestDto
{
    /// <summary>Content of the blog comment (1..1000 characters).</summary>
    public string Content { get; set; } = string.Empty;
}
