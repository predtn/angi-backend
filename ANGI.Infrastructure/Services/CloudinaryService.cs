using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Models;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.Extensions.Configuration;

namespace ANGI.Infrastructure.Services;

public sealed class CloudinaryService : ICloudinaryService
{
    private readonly ICloudinaryClient _client;

    /// <summary>Initializes Cloudinary storage from application configuration.</summary>
    public CloudinaryService(IConfiguration configuration)
        : this(CreateClient(configuration))
    {
    }

    /// <summary>Initializes Cloudinary storage with an injectable client for deterministic tests.</summary>
    internal CloudinaryService(ICloudinaryClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>Creates and validates the Cloudinary SDK adapter from configuration values.</summary>
    private static ICloudinaryClient CreateClient(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var cloudName = configuration["Cloudinary:CloudName"];
        var apiKey = configuration["Cloudinary:ApiKey"];
        var apiSecret = configuration["Cloudinary:ApiSecret"];

        if (string.IsNullOrWhiteSpace(cloudName) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(apiSecret))
        {
            throw new InvalidOperationException("Cloudinary configuration is incomplete.");
        }

        return new CloudinaryClient(new Account(cloudName, apiKey, apiSecret));
    }

    /// <summary>Uploads an image or PDF and returns its stable storage key and delivery URL.</summary>
    public async Task<MediaUploadResult> UploadAsync(
        Stream file,
        string fileName,
        string mimeType,
        string folder,
        bool isPrivate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedFolder = folder.Trim().Trim('/');
        if (normalizedFolder.Length == 0)
        {
            throw new ArgumentException("Folder must contain at least one character.", nameof(folder));
        }

        var isPdf = string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase);
        var publicId = $"{normalizedFolder}/{Guid.NewGuid():N}";
        var deliveryType = isPrivate ? "authenticated" : "upload";
        var fileDescription = new FileDescription(fileName, file);

        UploadResult result;
        try
        {
            result = isPdf
                ? await _client.UploadRawAsync(new RawUploadParams
                {
                    File = fileDescription,
                    PublicId = $"{publicId}.pdf",
                    Type = deliveryType
                }, cancellationToken)
                : await _client.UploadImageAsync(new ImageUploadParams
                {
                    File = fileDescription,
                    PublicId = publicId,
                    Type = deliveryType
                }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ServiceUnavailableException(
                "SERVICE_UNAVAILABLE",
                "Dịch vụ lưu trữ tạm thời không khả dụng.");
        }

        if (result.Error is not null ||
            string.IsNullOrWhiteSpace(result.PublicId) ||
            (!isPdf && string.IsNullOrWhiteSpace(result.Format)))
        {
            throw new ServiceUnavailableException(
                "SERVICE_UNAVAILABLE",
                "Dịch vụ lưu trữ trả về phản hồi không hợp lệ.");
        }

        var resourceType = isPdf ? "raw" : "image";
        var assetId = isPdf ? result.PublicId : $"{result.PublicId}.{result.Format}";
        var storageKey = $"{resourceType}/{deliveryType}/{assetId}";
        var url = result.SecureUrl?.ToString();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ServiceUnavailableException(
                "SERVICE_UNAVAILABLE",
                "Dịch vụ lưu trữ trả về URL không hợp lệ.");
        }

        return new MediaUploadResult(storageKey, url);
    }

    /// <summary>Builds a public URL or a five-minute signed URL from a stable storage key.</summary>
    public string GetUrl(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        var parts = storageKey.Split('/', 3);
        if (parts.Length != 3 ||
            (parts[0] != "image" && parts[0] != "raw") ||
            (parts[1] != "upload" && parts[1] != "authenticated") ||
            string.IsNullOrWhiteSpace(parts[2]))
        {
            throw new ArgumentException("Invalid Cloudinary storage key.", nameof(storageKey));
        }

        var format = parts[0] == "image" ? Path.GetExtension(parts[2]).TrimStart('.') : null;
        if (parts[0] == "image" && string.IsNullOrWhiteSpace(format))
        {
            throw new ArgumentException("Invalid Cloudinary image storage key.", nameof(storageKey));
        }

        var publicId = parts[0] == "image"
            ? parts[2][..^(format!.Length + 1)]
            : parts[2];

        if (parts[1] == "authenticated")
        {
            return _client.DownloadPrivate(
                publicId,
                format: format,
                type: parts[1],
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
                resourceType: parts[0]);
        }

        return _client.BuildPublicUrl(
            parts[0],
            parts[1],
            format is null ? publicId : $"{publicId}.{format}");
    }
}

internal interface ICloudinaryClient
{
    /// <summary>Uploads an image resource to Cloudinary.</summary>
    Task<UploadResult> UploadImageAsync(
        ImageUploadParams parameters,
        CancellationToken cancellationToken);

    /// <summary>Uploads a raw resource such as a PDF to Cloudinary.</summary>
    Task<UploadResult> UploadRawAsync(
        RawUploadParams parameters,
        CancellationToken cancellationToken);

    /// <summary>Builds a signed URL for an authenticated Cloudinary resource.</summary>
    string DownloadPrivate(
        string publicId,
        string? format,
        string type,
        long expiresAt,
        string resourceType);

    /// <summary>Builds a URL for a publicly delivered Cloudinary resource.</summary>
    string BuildPublicUrl(string resourceType, string type, string publicId);
}

internal sealed class CloudinaryClient : ICloudinaryClient
{
    private readonly Cloudinary _cloudinary;

    /// <summary>Initializes the SDK adapter with secure URL generation enabled.</summary>
    public CloudinaryClient(Account account)
    {
        _cloudinary = new Cloudinary(account)
        {
            Api = { Secure = true }
        };
    }

    /// <summary>Uploads image parameters through the Cloudinary SDK.</summary>
    public async Task<UploadResult> UploadImageAsync(
        ImageUploadParams parameters,
        CancellationToken cancellationToken) =>
        await _cloudinary.UploadAsync(parameters, cancellationToken);

    /// <summary>Uploads raw parameters through the Cloudinary SDK.</summary>
    public async Task<UploadResult> UploadRawAsync(
        RawUploadParams parameters,
        CancellationToken cancellationToken) =>
        await _cloudinary.UploadAsync(parameters, "raw", cancellationToken);

    /// <summary>Delegates authenticated URL generation to the Cloudinary SDK.</summary>
    public string DownloadPrivate(
        string publicId,
        string? format,
        string type,
        long expiresAt,
        string resourceType) =>
        _cloudinary.DownloadPrivate(
            publicId,
            format: format,
            type: type,
            expiresAt: expiresAt,
            resourceType: resourceType);

    /// <summary>Delegates secure public URL generation to the Cloudinary SDK.</summary>
    public string BuildPublicUrl(string resourceType, string type, string publicId) =>
        _cloudinary.Api.UrlImgUp
            .ResourceType(resourceType)
            .Type(type)
            .Secure(true)
            .BuildUrl(publicId);
}
