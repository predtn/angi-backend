using Microsoft.AspNetCore.Http;

namespace ANGI.WebApi.Models.Media;

/// <summary>Represents the multipart/form-data boundary model for MEDIA-01.</summary>
public sealed class UploadMediaForm
{
    /// <summary>Null when the field is missing; the validator reports it as "file".</summary>
    public IFormFile? File { get; set; }
    public string Purpose { get; set; } = string.Empty;
}
