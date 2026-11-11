namespace ANGI.Application.DTOs.Social;

/// <summary>Request body of BLOG-10.</summary>
public sealed class CreateBlogCommentRequestDto
{
    /// <summary>Comment content (1..1000 characters).</summary>
    public string Content { get; set; } = string.Empty;
}
