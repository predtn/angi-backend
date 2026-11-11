using System.Text;
using ANGI.Infrastructure.Services.Cloudinary;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.Extensions.Configuration;

namespace ANGI.IntegrationTests.Infrastructure.Services.Cloudinary;

/// <summary>Exercises media upload and cleanup against a real Cloudinary account.</summary>
public class CloudinaryServiceLiveTests
{
    // TEST-01: Upload a public image and private PDF to a real account, then delete both assets. Area: Cloudinary live integration.
    /// <summary>Verifies real Cloudinary image and document uploads and cleans up the uploaded assets.</summary>
    [CloudinaryLiveFact]
    public async Task UploadsImageAndPrivatePdfToRealCloudinary()
    {
        // STEP-01: Get appsettingsPath from Path.GetFullPath and create configuration through AddEnvironmentVariables().Build.
        var appsettingsPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../ANGI.WebApi/appsettings.json"));
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(appsettingsPath)
            .AddUserSecrets(typeof(ANGI.WebApi.DependencyInjection).Assembly)
            .AddEnvironmentVariables()
            .Build();

        // STEP-02: Create the CloudinaryService under test as service.
        var service = new CloudinaryService(configuration);
        // STEP-03: Create cleanup as CloudinaryDotNet.Cloudinary, create uploaded as List<DeletionParams> and get png from Convert.FromBase64String.
        var cleanup = new CloudinaryDotNet.Cloudinary(new Account(
            configuration["Cloudinary:CloudName"],
            configuration["Cloudinary:ApiKey"],
            configuration["Cloudinary:ApiSecret"]));
        var uploaded = new List<DeletionParams>();

        try
        {
            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lXcAAAAASUVORK5CYII=");
            // STEP-04: Assert result.StorageKey starts with "image/upload/angi/avatar/" and result.Url starts with "https://".
            using (var image = new MemoryStream(png))
            {
                var result = await service.UploadAsync(
                    image, "smoke.png", "image/png", "angi/avatar", false,
                    CancellationToken.None);
                uploaded.Add(ToDeletionParams(result.StorageKey));
                Assert.StartsWith("image/upload/angi/avatar/", result.StorageKey);
                Assert.StartsWith("https://", result.Url);
            }

            // STEP-05: Get pdf from Encoding.ASCII.GetBytes.
            var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\n%%EOF\n");
            // STEP-06: Assert result.StorageKey starts with the expected text, result.Url starts with "https://" and service.GetUrl() contains "expires_at".
            using (var document = new MemoryStream(pdf))
            {
                var result = await service.UploadAsync(
                    document, "smoke.pdf", "application/pdf",
                    "angi/verification_doc", true, CancellationToken.None);
                uploaded.Add(ToDeletionParams(result.StorageKey));
                Assert.StartsWith("raw/authenticated/angi/verification_doc/", result.StorageKey);
                Assert.StartsWith("https://", result.Url);
                Assert.Contains("expires_at", service.GetUrl(result.StorageKey));
            }
        }
        finally
        {
            // STEP-07: Clean up: for each item in uploaded, get deletion from cleanup.DestroyAsync and call Assert.Equal.
            foreach (var item in uploaded)
            {
                var deletion = await cleanup.DestroyAsync(item);
                Assert.Equal("ok", deletion.Result);
            }
        }
    }

    /// <summary>Converts a persisted storage key into the Cloudinary deletion request used for cleanup.</summary>
    private static DeletionParams ToDeletionParams(string storageKey)
    {
        var parts = storageKey.Split('/', 3);
        var publicId = parts[0] == "image"
            ? parts[2][..^Path.GetExtension(parts[2]).Length]
            : parts[2];
        return new DeletionParams(publicId)
        {
            ResourceType = parts[0] == "raw" ? ResourceType.Raw : ResourceType.Image,
            Type = parts[1]
        };
    }

}

/// <summary>Guards Cloudinary tests from making external calls unless explicitly enabled.</summary>
public sealed class CloudinaryLiveFactAttribute : FactAttribute
{
    /// <summary>Enables the live test only when Cloudinary execution is explicitly requested.</summary>
    public CloudinaryLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_CLOUDINARY_LIVE_TESTS") != "1")
        {
            Skip = "Set RUN_CLOUDINARY_LIVE_TESTS=1 to upload to Cloudinary.";
        }
    }
}
