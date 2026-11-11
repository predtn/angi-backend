namespace ANGI.Application.DTOs.Media;

/// <summary>Represents uploaded media metadata returned by MEDIA-01.</summary>
public sealed class MediaFileDto
{
    public long Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
