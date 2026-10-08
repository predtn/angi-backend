using Microsoft.AspNetCore.Http;

namespace ANGI.WebApi.Models.Media;

/// <summary>Represents the multipart/form-data boundary model for MEDIA-01.</summary>
public sealed class UploadMediaForm
{
    public IFormFile File { get; set; } = null!;
    public string Purpose { get; set; } = string.Empty;
}
