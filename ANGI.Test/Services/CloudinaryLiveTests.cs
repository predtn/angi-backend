using System.Text;
using ANGI.Infrastructure.Services;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.Extensions.Configuration;

namespace ANGI.Test.Services;

public class CloudinaryLiveTests
{
    // TEST-18: Upload a public image and private PDF to a real account, then delete both assets. Area: Cloudinary live integration.
    [CloudinaryLiveFact]
    public async Task UploadsImageAndPrivatePdfToRealCloudinary()
    {
        var appsettingsPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../ANGI.WebApi/appsettings.json"));
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(appsettingsPath)
            .AddUserSecrets(typeof(ANGI.WebApi.DependencyInjection).Assembly)
            .AddEnvironmentVariables()
            .Build();

        var service = new CloudinaryService(configuration);
        var cleanup = new Cloudinary(new Account(
            configuration["Cloudinary:CloudName"],
            configuration["Cloudinary:ApiKey"],
            configuration["Cloudinary:ApiSecret"]));
        var uploaded = new List<DeletionParams>();

        try
        {
            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lXcAAAAASUVORK5CYII=");
            using (var image = new MemoryStream(png))
            {
                var result = await service.UploadAsync(
                    image, "smoke.png", "image/png", "angi/avatar", false,
                    CancellationToken.None);
                uploaded.Add(ToDeletionParams(result.StorageKey));
                Assert.StartsWith("image/upload/angi/avatar/", result.StorageKey);
                Assert.StartsWith("https://", result.Url);
            }

            var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\n%%EOF\n");
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
            foreach (var item in uploaded)
            {
                var deletion = await cleanup.DestroyAsync(item);
                Assert.Equal("ok", deletion.Result);
            }
        }
    }

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

public sealed class CloudinaryLiveFactAttribute : FactAttribute
{
    public CloudinaryLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_CLOUDINARY_LIVE_TESTS") != "1")
        {
            Skip = "Set RUN_CLOUDINARY_LIVE_TESTS=1 to upload to Cloudinary.";
        }
    }
}
