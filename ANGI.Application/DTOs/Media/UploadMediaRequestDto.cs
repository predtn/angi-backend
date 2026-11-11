namespace ANGI.Application.DTOs.Media;

/// <summary>Contains the transport-neutral file data required by MEDIA-01.</summary>
public sealed class UploadMediaRequestDto
{
    /// <summary>Null when the multipart field "file" is missing.</summary>
    public Stream? Content { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Purpose { get; set; } = string.Empty;
}
