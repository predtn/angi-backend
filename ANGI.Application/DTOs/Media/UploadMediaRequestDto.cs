namespace ANGI.Application.DTOs.Media;

/// <summary>Contains the transport-neutral file data required by MEDIA-01.</summary>
public sealed class UploadMediaRequestDto
{
    public Stream Content { get; set; } = Stream.Null;
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Purpose { get; set; } = string.Empty;
}
