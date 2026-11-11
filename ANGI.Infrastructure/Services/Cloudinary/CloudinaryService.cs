using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Models;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.Extensions.Configuration;

namespace ANGI.Infrastructure.Services.Cloudinary;

/// <summary>Stores media in Cloudinary and resolves stable storage keys to delivery URLs.</summary>
public sealed class CloudinaryService : ICloudinaryService
{
    private readonly ICloudinaryClient _client;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes Cloudinary storage from application configuration.</summary>
    public CloudinaryService(IConfiguration configuration, TimeProvider? timeProvider = null)
        : this(CreateClient(configuration), timeProvider)
    {
    }

    /// <summary>Initializes Cloudinary storage with an injectable client for deterministic tests.</summary>
    internal CloudinaryService(ICloudinaryClient client, TimeProvider? timeProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Creates and validates the Cloudinary SDK adapter from configuration values.</summary>
    private static ICloudinaryClient CreateClient(IConfiguration configuration)
    {
        // STEP-01: Call ArgumentNullException.ThrowIfNull.
        ArgumentNullException.ThrowIfNull(configuration);

        // STEP-02: Prepare cloudName, apiKey and apiSecret.
        var cloudName = configuration["Cloudinary:CloudName"];
        var apiKey = configuration["Cloudinary:ApiKey"];
        var apiSecret = configuration["Cloudinary:ApiSecret"];

        // STEP-03: Throw InvalidOperationException when cloudName is blank, apiKey is blank or apiSecret is blank.
        if (string.IsNullOrWhiteSpace(cloudName) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(apiSecret))
        {
            throw new InvalidOperationException("Cloudinary configuration is incomplete.");
        }

        // STEP-04: Return a new CloudinaryClient.
        return new CloudinaryClient(new CloudinaryDotNet.Account(cloudName, apiKey, apiSecret));
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
        // STEP-01: Call ArgumentNullException.ThrowIfNull, ArgumentException.ThrowIfNullOrWhiteSpace and cancellationToken.ThrowIfCancellationRequested.
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        cancellationToken.ThrowIfCancellationRequested();

        // STEP-02: Prepare normalizedFolder through folder.Trim().Trim.
        var normalizedFolder = folder.Trim().Trim('/');
        // STEP-03: Throw ArgumentException when normalizedFolder.Length is 0.
        if (normalizedFolder.Length == 0)
        {
            throw new ArgumentException("Folder must contain at least one character.", nameof(folder));
        }

        // STEP-04: Prepare isPdf through string.Equals, publicId from Guid.NewGuid, deliveryType, fileDescription as FileDescription and 1 more.
        var isPdf = string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase);
        var publicId = $"{normalizedFolder}/{Guid.NewGuid():N}";
        var deliveryType = isPrivate ? "authenticated" : "upload";
        var fileDescription = new FileDescription(fileName, file);

        UploadResult result;
        try
        {
            // STEP-05: Set result.
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
            // STEP-06: On OperationCanceledException when cancellationToken.IsCancellationRequested is true, rethrow the caught exception.
            throw;
        }
        catch (Exception)
        {
            // STEP-07: On Exception, reject with SERVICE_UNAVAILABLE.
            throw new ServiceUnavailableException(
                "SERVICE_UNAVAILABLE",
                "Dịch vụ lưu trữ tạm thời không khả dụng.");
        }

        // STEP-08: Reject with SERVICE_UNAVAILABLE when result.Error exists, result.PublicId is blank or (isPdf is false and result.Format is blank).
        if (result.Error is not null ||
            string.IsNullOrWhiteSpace(result.PublicId) ||
            (!isPdf && string.IsNullOrWhiteSpace(result.Format)))
        {
            throw new ServiceUnavailableException(
                "SERVICE_UNAVAILABLE",
                "Dịch vụ lưu trữ trả về phản hồi không hợp lệ.");
        }

        // STEP-09: Prepare resourceType, assetId, storageKey and url from ToString.
        var resourceType = isPdf ? "raw" : "image";
        var assetId = isPdf ? result.PublicId : $"{result.PublicId}.{result.Format}";
        var storageKey = $"{resourceType}/{deliveryType}/{assetId}";
        var url = result.SecureUrl?.ToString();

        // STEP-10: Reject with SERVICE_UNAVAILABLE when Uri.TryCreate(url, UriKind.Absolute, ...) does not hold or uri.Scheme is not Uri.UriSchemeHttps.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ServiceUnavailableException(
                "SERVICE_UNAVAILABLE",
                "Dịch vụ lưu trữ trả về URL không hợp lệ.");
        }

        // STEP-11: Return a new MediaUploadResult.
        return new MediaUploadResult(storageKey, url);
    }

    /// <summary>Builds a public URL or a five-minute signed URL from a stable storage key.</summary>
    public string GetUrl(string storageKey)
    {
        // STEP-01: Call ArgumentException.ThrowIfNullOrWhiteSpace.
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        // STEP-02: Prepare parts through storageKey.Split.
        var parts = storageKey.Split('/', 3);
        // STEP-03: Throw ArgumentException when parts.Length is not 3, (parts[0] is not "image" and parts[0] is not "raw"), (parts[1] is not "upload" and more.
        if (parts.Length != 3 ||
            (parts[0] != "image" && parts[0] != "raw") ||
            (parts[1] != "upload" && parts[1] != "authenticated") ||
            string.IsNullOrWhiteSpace(parts[2]))
        {
            throw new ArgumentException("Invalid Cloudinary storage key.", nameof(storageKey));
        }

        // STEP-04: Prepare format from Path.GetExtension.
        var format = parts[0] == "image" ? Path.GetExtension(parts[2]).TrimStart('.') : null;
        // STEP-05: Throw ArgumentException when parts[0] is "image" and format is blank.
        if (parts[0] == "image" && string.IsNullOrWhiteSpace(format))
        {
            throw new ArgumentException("Invalid Cloudinary image storage key.", nameof(storageKey));
        }

        // STEP-06: Prepare publicId.
        var publicId = parts[0] == "image"
            ? parts[2][..^(format!.Length + 1)]
            : parts[2];

        // STEP-07: Return the result of client.DownloadPrivate early when parts[1] is "authenticated".
        if (parts[1] == "authenticated")
        {
            return _client.DownloadPrivate(
                publicId,
                format: format,
                type: parts[1],
                expiresAt: _timeProvider.GetUtcNow().AddMinutes(5).ToUnixTimeSeconds(),
                resourceType: parts[0]);
        }

        // STEP-08: Return the result of client.BuildPublicUrl.
        return _client.BuildPublicUrl(
            parts[0],
            parts[1],
            format is null ? publicId : $"{publicId}.{format}");
    }
}

/// <summary>Abstracts the Cloudinary SDK operations used by the storage service.</summary>
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

/// <summary>Adapts the Cloudinary SDK to the testable storage client abstraction.</summary>
internal sealed class CloudinaryClient : ICloudinaryClient
{
    private readonly CloudinaryDotNet.Cloudinary _cloudinary;

    /// <summary>Initializes the SDK adapter with secure URL generation enabled.</summary>
    public CloudinaryClient(CloudinaryDotNet.Account account)
    {
        _cloudinary = new CloudinaryDotNet.Cloudinary(account)
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
