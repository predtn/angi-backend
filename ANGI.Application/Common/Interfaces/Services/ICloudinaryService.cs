using ANGI.Application.Common.Models;

namespace ANGI.Application.Common.Interfaces.Services;

public interface ICloudinaryService
{
    /// <summary>Uploads content and returns the stable storage key and URL persisted in media_files.</summary>
    Task<MediaUploadResult> UploadAsync(
        Stream file,
        string fileName,
        string mimeType,
        string folder,
        bool isPrivate,
        CancellationToken cancellationToken);

    /// <summary>Generates a URL for stored media, signing private resources for five minutes.</summary>
    string GetUrl(string storageKey);
}
