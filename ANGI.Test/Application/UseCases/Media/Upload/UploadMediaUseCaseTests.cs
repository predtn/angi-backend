using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Media;
using ANGI.Application.UseCases.Media.Upload;
using ANGI.Application.UseCases.Validators.Media;
using ANGI.Domain.Entities;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.Test.Application.UseCases.Media.Upload;

public sealed class UploadMediaUseCaseTests
{
    private static readonly DateTime CreatedAt = new(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);

    // TEST-01: Upload and persist a supported image for the authenticated user.
    /// <summary>Verifies the complete successful MEDIA-01 workflow.</summary>
    [Fact]
    public async Task ExecuteAsync_WithValidImage_ShouldUploadPersistAndReturnMetadata()
    {
        var cloudinary = new Mock<ICloudinaryService>();
        cloudinary.Setup(service => service.UploadAsync(
                It.IsAny<Stream>(),
                "photo.jpg",
                "image/jpeg",
                "restaurant",
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaUploadResult("image/upload/media-key.jpg", "https://cdn.test/media-key.jpg"));
        var repository = new Mock<IMediaRepository>();
        MediaFile? addedMedia = null;
        repository.Setup(repo => repo.Add(It.IsAny<MediaFile>()))
            .Callback<MediaFile>(media => addedMedia = media);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                addedMedia!.Id = 91;
                addedMedia.CreatedAt = CreatedAt;
            })
            .ReturnsAsync(1);
        var useCase = CreateUseCase(cloudinary, repository, unitOfWork);

        var result = await useCase.ExecuteAsync(ValidImageRequest(), CancellationToken.None);

        result.Id.Should().Be(91);
        result.Url.Should().Be("https://cdn.test/media-key.jpg");
        result.MimeType.Should().Be("image/jpeg");
        result.SizeBytes.Should().Be(1024);
        result.Purpose.Should().Be("restaurant");
        result.CreatedAt.Should().Be(CreatedAt);
        addedMedia!.UploaderId.Should().Be(12);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Reject an image larger than five megabytes before calling storage.
    /// <summary>Verifies the documented FILE_TOO_LARGE error and prevents side effects.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOversizedImage_ShouldThrowFileTooLarge()
    {
        var cloudinary = new Mock<ICloudinaryService>();
        var repository = new Mock<IMediaRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = CreateUseCase(cloudinary, repository, unitOfWork);
        var request = ValidImageRequest();
        request.SizeBytes = (5 * 1024 * 1024) + 1;

        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be("FILE_TOO_LARGE");
        cloudinary.Verify(service => service.UploadAsync(
            It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Allow PDF files only for verification documents.
    /// <summary>Verifies the purpose-specific PDF restriction from MEDIA-01.</summary>
    [Fact]
    public async Task ExecuteAsync_WithPdfForRestaurantPurpose_ShouldThrowUnsupportedMediaType()
    {
        var useCase = CreateUseCase(
            new Mock<ICloudinaryService>(),
            new Mock<IMediaRepository>(),
            new Mock<IUnitOfWork>());
        var request = ValidImageRequest();
        request.FileName = "document.pdf";
        request.MimeType = "application/pdf";

        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be("UNSUPPORTED_MEDIA_TYPE");
    }

    // TEST-04: Normalize a case-insensitive PDF media type before calling storage.
    /// <summary>Verifies that a valid verification PDF is consistently treated as a private raw upload.</summary>
    [Fact]
    public async Task ExecuteAsync_WithMixedCasePdfMimeType_ShouldNormalizeBeforeUpload()
    {
        var cloudinary = new Mock<ICloudinaryService>();
        cloudinary.Setup(service => service.UploadAsync(
                It.IsAny<Stream>(),
                "document.pdf",
                "application/pdf",
                "verification_doc",
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaUploadResult(
                "raw/authenticated/verification_doc/document.pdf",
                "https://cdn.test/document.pdf"));
        var repository = new Mock<IMediaRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = CreateUseCase(cloudinary, repository, unitOfWork);
        var request = ValidImageRequest();
        request.FileName = "document.pdf";
        request.MimeType = "Application/PDF";
        request.Purpose = "verification_doc";

        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        result.MimeType.Should().Be("application/pdf");
        cloudinary.VerifyAll();
    }

    // TEST-05: Run FluentValidation before reading authentication or external storage.
    /// <summary>Verifies that an unsupported purpose fails at the first use-case step.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidPurpose_ShouldThrowValidationExceptionFirst()
    {
        var currentUser = new Mock<ICurrentUserService>();
        var useCase = new UploadMediaUseCase(
            new UploadMediaRequestDtoValidator(),
            new Mock<ICloudinaryService>().Object,
            new Mock<IMediaRepository>().Object,
            currentUser.Object,
            new Mock<IUnitOfWork>().Object);
        var request = ValidImageRequest();
        request.Purpose = "unknown";

        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        currentUser.VerifyGet(service => service.UserId, Times.Never);
    }

    // TEST-06: Propagate a database failure after Cloudinary has accepted the file.
    // Target: Cloudinary -> repository -> UnitOfWork call order and the post-upload failure boundary.
    /// <summary>Documents the failure boundary where the Cloudinary upload succeeded but metadata persistence failed.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenSaveChangesFailsAfterUpload_ShouldPropagateDatabaseFailure()
    {
        var cloudinary = new Mock<ICloudinaryService>();
        cloudinary.Setup(service => service.UploadAsync(
                It.IsAny<Stream>(),
                "photo.jpg",
                "image/jpeg",
                "restaurant",
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaUploadResult(
                "image/upload/restaurant/orphaned-media.jpg",
                "https://cdn.test/orphaned-media.jpg"));
        var repository = new Mock<IMediaRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Database write failed."));
        var useCase = CreateUseCase(cloudinary, repository, unitOfWork);

        var act = () => useCase.ExecuteAsync(ValidImageRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Database write failed.");
        cloudinary.Verify(service => service.UploadAsync(
            It.IsAny<Stream>(),
            "photo.jpg",
            "image/jpeg",
            "restaurant",
            false,
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(repo => repo.Add(It.Is<MediaFile>(media =>
            media.StorageKey == "image/upload/restaurant/orphaned-media.jpg")), Times.Once);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Creates a use case with an authenticated test user.</summary>
    private static UploadMediaUseCase CreateUseCase(
        Mock<ICloudinaryService> cloudinary,
        Mock<IMediaRepository> repository,
        Mock<IUnitOfWork> unitOfWork)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(service => service.UserId).Returns(12);
        return new UploadMediaUseCase(
            new UploadMediaRequestDtoValidator(),
            cloudinary.Object,
            repository.Object,
            currentUser.Object,
            unitOfWork.Object);
    }

    /// <summary>Creates a minimal valid image upload request.</summary>
    private static UploadMediaRequestDto ValidImageRequest() => new()
    {
        Content = new MemoryStream(new byte[1024]),
        FileName = "photo.jpg",
        MimeType = "image/jpeg",
        SizeBytes = 1024,
        Purpose = "restaurant"
    };
}
