using System.Text.RegularExpressions;
using ANGI.Application.Common.Exceptions;
using ANGI.Infrastructure.Services;
using CloudinaryDotNet.Actions;
using Microsoft.Extensions.Configuration;

namespace ANGI.Test.Infrastructure.Services;

public class CloudinaryServiceTests
{
    // TEST-01: Reject a null configuration dependency. Area: Cloudinary service configuration.
    [Fact]
    public void Constructor_RejectsNullConfiguration()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CloudinaryService((IConfiguration)null!));
    }

    // TEST-02: Reject any missing required Cloudinary credential. Area: Cloudinary service configuration.
    [Theory]
    [InlineData("Cloudinary:CloudName")]
    [InlineData("Cloudinary:ApiKey")]
    [InlineData("Cloudinary:ApiSecret")]
    public void Constructor_RejectsIncompleteConfiguration(string missingKey)
    {
        var values = ValidConfigurationValues();
        values[missingKey] = "";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Throws<InvalidOperationException>(() => new CloudinaryService(configuration));
    }

    // TEST-03: Reject a null upload stream before contacting Cloudinary. Area: Upload input validation.
    [Fact]
    public async Task UploadAsync_RejectsNullFileBeforeProviderCall()
    {
        var client = new FakeCloudinaryClient();
        var service = new CloudinaryService(client);

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.UploadAsync(
            null!, "photo.png", "image/png", "angi/avatar", false, CancellationToken.None));
        Assert.Equal(0, client.UploadCallCount);
    }

    // TEST-04: Reject a blank file name before contacting Cloudinary. Area: Upload input validation.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UploadAsync_RejectsInvalidFileNameBeforeProviderCall(string fileName)
    {
        var client = new FakeCloudinaryClient();
        var service = new CloudinaryService(client);
        using var file = new MemoryStream([1]);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.UploadAsync(
            file, fileName, "image/png", "angi/avatar", false, CancellationToken.None));
        Assert.Equal(0, client.UploadCallCount);
    }

    // TEST-05: Reject a blank MIME type before contacting Cloudinary. Area: Upload input validation.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UploadAsync_RejectsInvalidMimeTypeBeforeProviderCall(string mimeType)
    {
        var client = new FakeCloudinaryClient();
        var service = new CloudinaryService(client);
        using var file = new MemoryStream([1]);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.UploadAsync(
            file, "photo.png", mimeType, "angi/avatar", false, CancellationToken.None));
        Assert.Equal(0, client.UploadCallCount);
    }

    // TEST-06: Reject an empty folder after trimming separators and whitespace. Area: Upload input validation.
    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("///")]
    [InlineData(" / ")]
    public async Task UploadAsync_RejectsInvalidFolderBeforeProviderCall(string folder)
    {
        var client = new FakeCloudinaryClient();
        var service = new CloudinaryService(client);
        using var file = new MemoryStream([1]);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.UploadAsync(
            file, "photo.png", "image/png", folder, false, CancellationToken.None));
        Assert.Equal(0, client.UploadCallCount);
    }

    // TEST-07: Honor caller cancellation before contacting Cloudinary. Area: Upload cancellation handling.
    [Fact]
    public async Task UploadAsync_PropagatesCallerCancellationBeforeProviderCall()
    {
        var client = new FakeCloudinaryClient();
        var service = new CloudinaryService(client);
        using var file = new MemoryStream([1]);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UploadAsync(
            file, "photo.png", "image/png", "angi/avatar", false, source.Token));
        Assert.Equal(0, client.UploadCallCount);
    }

    // TEST-08: Map a public image upload to the image endpoint and a persistent storage key. Area: Image upload mapping.
    [Fact]
    public async Task UploadAsync_MapsPublicImageUploadAndResult()
    {
        var client = new FakeCloudinaryClient
        {
            ImageResult = new ImageUploadResult
            {
                PublicId = "angi/avatar/generated-id",
                Format = "png",
                SecureUrl = new Uri("https://cdn.example.com/angi/avatar/generated-id.png")
            }
        };
        var service = new CloudinaryService(client);
        using var file = new MemoryStream([1, 2, 3]);

        var result = await service.UploadAsync(
            file, "photo.png", "image/png", "/angi/avatar/", false, CancellationToken.None);

        Assert.Equal("image/upload/angi/avatar/generated-id.png", result.StorageKey);
        Assert.Equal("https://cdn.example.com/angi/avatar/generated-id.png", result.Url);
        Assert.NotNull(client.ImageParameters);
        Assert.Equal("upload", client.ImageParameters.Type);
        Assert.Matches(@"^angi/avatar/[0-9a-f]{32}$", client.ImageParameters.PublicId);
        Assert.Equal(0, client.RawUploadCallCount);
    }

    // TEST-09: Map a private PDF upload to the raw authenticated endpoint. Area: Document upload mapping.
    [Fact]
    public async Task UploadAsync_MapsPrivatePdfUploadAndResult()
    {
        var client = new FakeCloudinaryClient
        {
            RawResult = new RawUploadResult
            {
                PublicId = "angi/verification_doc/generated-id.pdf",
                SecureUrl = new Uri("https://cdn.example.com/angi/verification_doc/generated-id.pdf")
            }
        };
        var service = new CloudinaryService(client);
        using var file = new MemoryStream([1, 2, 3]);

        var result = await service.UploadAsync(
            file, "identity.pdf", "application/pdf", "angi/verification_doc", true,
            CancellationToken.None);

        Assert.Equal("raw/authenticated/angi/verification_doc/generated-id.pdf", result.StorageKey);
        Assert.NotNull(client.RawParameters);
        Assert.Equal("authenticated", client.RawParameters.Type);
        Assert.Matches(@"^angi/verification_doc/[0-9a-f]{32}\.pdf$", client.RawParameters.PublicId);
        Assert.Equal(0, client.ImageUploadCallCount);
    }

    // TEST-10: Treat media types as case-insensitive at the provider boundary.
    [Fact]
    public async Task UploadAsync_WithMixedCasePdfMimeType_ShouldUseRawUpload()
    {
        var client = new FakeCloudinaryClient
        {
            RawResult = new RawUploadResult
            {
                PublicId = "angi/verification_doc/generated-id.pdf",
                SecureUrl = new Uri("https://cdn.example.com/angi/verification_doc/generated-id.pdf")
            }
        };
        var service = new CloudinaryService(client);
        using var file = new MemoryStream([1, 2, 3]);

        await service.UploadAsync(
            file, "identity.pdf", "Application/PDF", "angi/verification_doc", true,
            CancellationToken.None);

        Assert.Equal(1, client.RawUploadCallCount);
        Assert.Equal(0, client.ImageUploadCallCount);
    }

    // TEST-11: Convert an unexpected Cloudinary failure into the application service-unavailable error. Area: Provider error handling.
    [Fact]
    public async Task UploadAsync_ConvertsProviderExceptionToServiceUnavailable()
    {
        var client = new FakeCloudinaryClient
        {
            UploadException = new HttpRequestException("Provider is unavailable.")
        };
        var service = new CloudinaryService(client);
        using var file = new MemoryStream([1]);

        var exception = await Assert.ThrowsAsync<ServiceUnavailableException>(() => service.UploadAsync(
            file, "photo.png", "image/png", "angi/avatar", false, CancellationToken.None));

        Assert.Equal("SERVICE_UNAVAILABLE", exception.ErrorCode);
    }

    // TEST-12: Preserve cancellation raised while the provider call is running. Area: Upload cancellation handling.
    [Fact]
    public async Task UploadAsync_PropagatesCancellationRaisedByProvider()
    {
        using var source = new CancellationTokenSource();
        var client = new FakeCloudinaryClient
        {
            BeforeUpload = source.Cancel,
            UploadException = new OperationCanceledException(source.Token)
        };
        var service = new CloudinaryService(client);
        using var file = new MemoryStream([1]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UploadAsync(
            file, "photo.png", "image/png", "angi/avatar", false, source.Token));
    }

    // TEST-13: Reject provider responses that contain errors or omit required asset metadata. Area: Provider response validation.
    [Theory]
    [MemberData(nameof(InvalidImageResults))]
    public async Task UploadAsync_RejectsInvalidProviderResult(UploadResult providerResult)
    {
        var client = new FakeCloudinaryClient { ImageResult = providerResult };
        var service = new CloudinaryService(client);
        using var file = new MemoryStream([1]);

        var exception = await Assert.ThrowsAsync<ServiceUnavailableException>(() => service.UploadAsync(
            file, "photo.png", "image/png", "angi/avatar", false, CancellationToken.None));

        Assert.Equal("SERVICE_UNAVAILABLE", exception.ErrorCode);
    }

    // TEST-14: Reject a provider response whose asset URL is not absolute HTTPS. Area: Provider response validation.
    [Theory]
    [InlineData("http://cdn.example.com/photo.png")]
    [InlineData("relative/photo.png")]
    public async Task UploadAsync_RejectsNonHttpsProviderUrl(string providerUrl)
    {
        var client = new FakeCloudinaryClient
        {
            ImageResult = new ImageUploadResult
            {
                PublicId = "angi/avatar/generated-id",
                Format = "png",
                SecureUrl = new Uri(providerUrl, UriKind.RelativeOrAbsolute)
            }
        };
        var service = new CloudinaryService(client);
        using var file = new MemoryStream([1]);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => service.UploadAsync(
            file, "photo.png", "image/png", "angi/avatar", false, CancellationToken.None));
    }

    // TEST-15: Build a stable HTTPS delivery URL for a public image key. Area: Public media URL generation.
    [Fact]
    public void GetUrl_BuildsStablePublicImageUrl()
    {
        var service = CreateService();
        var url = service.GetUrl("image/upload/angi/avatar/example.png");

        Assert.StartsWith("https://", url);
        Assert.EndsWith("/angi/avatar/example.png", url);
    }

    // TEST-16: Build a stable HTTPS delivery URL for a public raw document key. Area: Public media URL generation.
    [Fact]
    public void GetUrl_BuildsStablePublicRawUrl()
    {
        var service = CreateService();
        var url = service.GetUrl("raw/upload/angi/document/example.pdf");

        Assert.StartsWith("https://", url);
        Assert.EndsWith("/angi/document/example.pdf", url);
    }

    // TEST-17: Create a signed URL that expires in about five minutes for private media. Area: Private media URL generation.
    [Theory]
    [InlineData("raw/authenticated/angi/verification_doc/example.pdf")]
    [InlineData("image/authenticated/angi/avatar/example.png")]
    public void GetUrl_CreatesPrivateUrlThatExpiresInAboutFiveMinutes(string storageKey)
    {
        var service = CreateService();
        var earliest = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var url = service.GetUrl(storageKey);

        var match = Regex.Match(url, @"(?:\?|&)expires_at=(\d+)");
        Assert.True(match.Success);
        Assert.InRange(
            long.Parse(match.Groups[1].Value),
            earliest,
            DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
        Assert.DoesNotContain("unit-test-placeholder", url);
    }

    // TEST-18: Reject malformed or unsupported storage keys. Area: Media storage-key validation.
    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("video/upload/angi/file.mp4")]
    [InlineData("image/unknown/angi/file.png")]
    [InlineData("image/upload/angi/file")]
    [InlineData("raw/upload/")]
    public void GetUrl_RejectsInvalidStorageKey(string storageKey)
    {
        var service = CreateService();

        Assert.ThrowsAny<ArgumentException>(() => service.GetUrl(storageKey));
    }

    public static TheoryData<UploadResult> InvalidImageResults() => new()
    {
        new ImageUploadResult
        {
            Error = new Error { Message = "Invalid upload." },
            PublicId = "angi/avatar/id",
            Format = "png",
            SecureUrl = new Uri("https://cdn.example.com/photo.png")
        },
        new ImageUploadResult
        {
            PublicId = "",
            Format = "png",
            SecureUrl = new Uri("https://cdn.example.com/photo.png")
        },
        new ImageUploadResult
        {
            PublicId = "angi/avatar/id",
            Format = "",
            SecureUrl = new Uri("https://cdn.example.com/photo.png")
        }
    };

    private static CloudinaryService CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(ValidConfigurationValues())
            .Build();
        return new CloudinaryService(configuration);
    }

    private static Dictionary<string, string?> ValidConfigurationValues() => new()
    {
        ["Cloudinary:CloudName"] = "test-cloud",
        ["Cloudinary:ApiKey"] = "test-key",
        ["Cloudinary:ApiSecret"] = "unit-test-placeholder"
    };

    private sealed class FakeCloudinaryClient : ICloudinaryClient
    {
        public UploadResult ImageResult { get; init; } = new ImageUploadResult();
        public UploadResult RawResult { get; init; } = new RawUploadResult();
        public Exception? UploadException { get; init; }
        public Action? BeforeUpload { get; init; }
        public ImageUploadParams? ImageParameters { get; private set; }
        public RawUploadParams? RawParameters { get; private set; }
        public int ImageUploadCallCount { get; private set; }
        public int RawUploadCallCount { get; private set; }
        public int UploadCallCount => ImageUploadCallCount + RawUploadCallCount;

        public Task<UploadResult> UploadImageAsync(
            ImageUploadParams parameters,
            CancellationToken cancellationToken)
        {
            ImageUploadCallCount++;
            ImageParameters = parameters;
            return CompleteUpload(ImageResult);
        }

        public Task<UploadResult> UploadRawAsync(
            RawUploadParams parameters,
            CancellationToken cancellationToken)
        {
            RawUploadCallCount++;
            RawParameters = parameters;
            return CompleteUpload(RawResult);
        }

        public string DownloadPrivate(
            string publicId,
            string? format,
            string type,
            long expiresAt,
            string resourceType) => throw new NotSupportedException();

        public string BuildPublicUrl(string resourceType, string type, string publicId) =>
            throw new NotSupportedException();

        private Task<UploadResult> CompleteUpload(UploadResult result)
        {
            BeforeUpload?.Invoke();
            return UploadException is null
                ? Task.FromResult(result)
                : Task.FromException<UploadResult>(UploadException);
        }
    }
}
