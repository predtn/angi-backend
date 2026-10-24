namespace ANGI.Application.DTOs.Social;

/// <summary>Response body of BLOG-08 and BLOG-09.</summary>
public sealed class BlogLikeResponseDto
{
    /// <summary>Indicates whether the blog is liked by the current user.</summary>
    public bool Liked { get; set; }

    /// <summary>Total like count after the operation.</summary>
    public int LikeCount { get; set; }
}
