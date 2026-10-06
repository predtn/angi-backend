using ANGI.Application.Common.Models;

namespace ANGI.Application.Common.Interfaces.Services;

public interface ICloudinaryService
{
    // Returns the stable storage key and URL that the caller persists in media_files.
    Task<MediaUploadResult> UploadAsync(
        Stream file,
        string fileName,
        string mimeType,
        string folder,
        bool isPrivate,
        CancellationToken cancellationToken);

    // Generate a fresh URL when returning private media; its signed URL expires after five minutes.
    string GetUrl(string storageKey);
}
