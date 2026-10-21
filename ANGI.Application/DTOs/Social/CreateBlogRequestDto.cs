namespace ANGI.Application.DTOs.Social;

/// <summary>Request payload for BLOG-04 (creating a new blog post or sharing a roadmap).</summary>
public sealed class CreateBlogRequestDto
{
    /// <summary>Tiêu đề bài viết (1..255 ký tự).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Nội dung bài viết (HTML/Markdown).</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>ID ảnh bìa đại diện (từ media_files, purpose = blog).</summary>
    public long? CoverMediaId { get; set; }

    /// <summary>Danh sách ID ảnh đính kèm trong bài viết (tối đa 20 ảnh).</summary>
    public List<long>? ImageMediaIds { get; set; }

    /// <summary>ID lộ trình muốn chia sẻ (UC Share My Roadmap).</summary>
    public long? RoadmapId { get; set; }
}
