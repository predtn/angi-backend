using System.Text.RegularExpressions;
using ANGI.Application.Common.Exceptions;
using ANGI.Infrastructure.Services.Cloudinary;
using CloudinaryDotNet.Actions;
using Microsoft.Extensions.Configuration;

namespace ANGI.UnitTests.Infrastructure.Services.Cloudinary;

/// <summary>Verifies CloudinaryService validates uploads, maps provider results and builds public or signed delivery URLs.</summary>
public class CloudinaryServiceTests
{
    // TEST-01: Reject a null configuration dependency. Area: Cloudinary service configuration.
    /// <summary>Ensures the service cannot be created without configuration.</summary>
    [Fact]
    public void Constructor_RejectsNullConfiguration()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CloudinaryService((IConfiguration)null!));
    }

    // TEST-02: Reject any missing required Cloudinary credential. Area: Cloudinary service configuration.
    /// <summary>Ensures a missing credential fails at startup instead of on the first upload.</summary>
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
    /// <summary>Ensures a missing stream never reaches Cloudinary.</summary>
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
    /// <summary>Ensures a blank file name never reaches Cloudinary.</summary>
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
    /// <summary>Ensures a blank MIME type never reaches Cloudinary.</summary>
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
    /// <summary>Ensures uploads always land in a real folder.</summary>
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
    /// <summary>Ensures a cancelled request does not start an upload.</summary>
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
    /// <summary>Ensures images use the image endpoint and return a persistent storage key.</summary>
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
    /// <summary>Ensures PDFs are stored privately behind authenticated delivery.</summary>
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
    /// <summary>Ensures letter case cannot route a PDF to the public image endpoint.</summary>
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
    /// <summary>Ensures provider failures surface as the documented 503.</summary>
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
    /// <summary>Ensures cancellation during the upload is not reported as an outage.</summary>
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
    /// <summary>Ensures an incomplete provider response is never stored as media.</summary>
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
    /// <summary>Ensures stored media URLs are always absolute HTTPS.</summary>
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
    /// <summary>Ensures public image URLs are stable for caching.</summary>
    [Fact]
    public void GetUrl_BuildsStablePublicImageUrl()
    {
        var service = CreateService();
        var url = service.GetUrl("image/upload/angi/avatar/example.png");

        Assert.StartsWith("https://", url);
        Assert.EndsWith("/angi/avatar/example.png", url);
    }

    // TEST-16: Build a stable HTTPS delivery URL for a public raw document key. Area: Public media URL generation.
    /// <summary>Ensures public document URLs are stable for caching.</summary>
    [Fact]
    public void GetUrl_BuildsStablePublicRawUrl()
    {
        var service = CreateService();
        var url = service.GetUrl("raw/upload/angi/document/example.pdf");

        Assert.StartsWith("https://", url);
        Assert.EndsWith("/angi/document/example.pdf", url);
    }

    // TEST-17: Create a signed URL that expires in about five minutes for private media. Area: Private media URL generation.
    /// <summary>Ensures private documents are only reachable through short-lived signed URLs.</summary>
    [Theory]
    [InlineData("raw/authenticated/angi/verification_doc/example.pdf")]
    [InlineData("image/authenticated/angi/avatar/example.png")]
    public void GetUrl_CreatesPrivateUrlThatExpiresInAboutFiveMinutes(string storageKey)
    {
        var now = new DateTimeOffset(2026, 10, 14, 1, 2, 3, TimeSpan.Zero);
        var service = CreateService(new FixedTimeProvider(now));
        var url = service.GetUrl(storageKey);

        var match = Regex.Match(url, @"(?:\?|&)expires_at=(\d+)");
        Assert.True(match.Success);
        Assert.Equal(now.AddMinutes(5).ToUnixTimeSeconds(), long.Parse(match.Groups[1].Value));
        Assert.DoesNotContain("unit-test-placeholder", url);
    }

    // TEST-18: Reject malformed or unsupported storage keys. Area: Media storage-key validation.
    /// <summary>Ensures malformed storage keys cannot produce delivery URLs.</summary>
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

    /// <summary>Supplies provider results that carry an error or miss required asset metadata.</summary>
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

    /// <summary>Creates the service with valid test credentials and an optional clock.</summary>
    private static CloudinaryService CreateService(TimeProvider? timeProvider = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(ValidConfigurationValues())
            .Build();
        return new CloudinaryService(configuration, timeProvider);
    }

    /// <summary>Returns a complete set of placeholder Cloudinary settings.</summary>
    private static Dictionary<string, string?> ValidConfigurationValues() => new()
    {
        ["Cloudinary:CloudName"] = "test-cloud",
        ["Cloudinary:ApiKey"] = "test-key",
        ["Cloudinary:ApiSecret"] = "unit-test-placeholder"
    };

    /// <summary>Clock fixed at one instant so signed URL expiry is deterministic.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        /// <summary>Returns the fixed instant supplied to the constructor.</summary>
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Cloudinary client double that records upload parameters and returns configured results or failures.</summary>
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

        /// <summary>Records the image upload parameters and completes with the configured image result.</summary>
        public Task<UploadResult> UploadImageAsync(
            ImageUploadParams parameters,
            CancellationToken cancellationToken)
        {
            ImageUploadCallCount++;
            ImageParameters = parameters;
            return CompleteUpload(ImageResult);
        }

        /// <summary>Records the raw upload parameters and completes with the configured raw result.</summary>
        public Task<UploadResult> UploadRawAsync(
            RawUploadParams parameters,
            CancellationToken cancellationToken)
        {
            RawUploadCallCount++;
            RawParameters = parameters;
            return CompleteUpload(RawResult);
        }

        /// <summary>Not used by these tests; throws if called.</summary>
        public string DownloadPrivate(
            string publicId,
            string? format,
            string type,
            long expiresAt,
            string resourceType) => throw new NotSupportedException();

        /// <summary>Not used by these tests; throws if called.</summary>
        public string BuildPublicUrl(string resourceType, string type, string publicId) =>
            throw new NotSupportedException();

        /// <summary>Runs the before-upload hook, then returns the result or the configured exception.</summary>
        private Task<UploadResult> CompleteUpload(UploadResult result)
        {
            BeforeUpload?.Invoke();
            return UploadException is null
                ? Task.FromResult(result)
                : Task.FromException<UploadResult>(UploadException);
        }
    }
}
