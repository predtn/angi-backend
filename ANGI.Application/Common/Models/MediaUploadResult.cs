namespace ANGI.Application.Common.Models;

// Url is the stable provider URL stored in media_files. Use ICloudinaryService.GetUrl
// when an authenticated asset needs a short-lived access URL.
public sealed record MediaUploadResult(string StorageKey, string Url);
