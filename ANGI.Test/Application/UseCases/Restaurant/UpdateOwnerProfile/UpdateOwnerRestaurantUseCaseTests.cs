using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.UpdateOwnerProfile;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.Test.Application.UseCases.Restaurant.UpdateOwnerProfile;

public sealed class UpdateOwnerRestaurantUseCaseTests
{
    // TEST-01: Update supplied fields, replace the gallery, and audit only changed values.
    /// <summary>Verifies the successful atomic OWN-03 update workflow.</summary>
    [Fact]
    public async Task ExecuteAsync_WithChangedFields_ShouldUpdateAndWriteAuditLog()
    {
        var restaurant = CreateRestaurant();
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetTrackedOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(item => item.GetOwnedByIdsAsync(12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaFile
            {
                Id = 102,
                UploaderId = 12,
                MimeType = "image/jpeg",
                Url = "https://cdn.test/new.jpg"
            }]);
        var auditService = new Mock<IAuditLogService>();
        AuditLogEntry? audit = null;
        auditService.Setup(item => item.Add(It.IsAny<AuditLogEntry>()))
            .Callback<AuditLogEntry>(entry => audit = entry);
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = CreateUseCase(repository, mediaRepository, auditService, unitOfWork);
        var request = new UpdateRestaurantRequestDto
        {
            Name = " Bún Bò Bà Diệu - Lê Duẩn ",
            ImageMediaIds = [102]
        };

        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        restaurant.Name.Should().Be("Bún Bò Bà Diệu - Lê Duẩn");
        restaurant.Images.Select(item => item.MediaId).Should().Equal(102);
        result.Images.Should().Equal("https://cdn.test/new.jpg");
        audit.Should().NotBeNull();
        audit!.Action.Should().Be(AuditActions.RestaurantUpdated);
        audit.EntityType.Should().Be(AuditEntityTypes.Restaurant);
        ((IDictionary<string, object?>)audit.OldValues!).Keys.Should().BeEquivalentTo("name", "image_media_ids");
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.GetOwnerProfileAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-02: Reject gallery media that does not belong to the authenticated owner.
    /// <summary>Verifies media ownership before mutating or saving the restaurant.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnownedMedia_ShouldThrowValidationException()
    {
        var restaurant = CreateRestaurant();
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetTrackedOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(item => item.GetOwnedByIdsAsync(12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MediaFile>());
        var auditService = new Mock<IAuditLogService>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = CreateUseCase(repository, mediaRepository, auditService, unitOfWork);

        var act = () => useCase.ExecuteAsync(
            new UpdateRestaurantRequestDto { CoverMediaId = 999 },
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be("CoverMediaId");
        auditService.Verify(item => item.Add(It.IsAny<AuditLogEntry>()), Times.Never);
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Skip persistence and audit when supplied values normalize to current values.
    /// <summary>Verifies that an idempotent PATCH does not create a false change record.</summary>
    [Fact]
    public async Task ExecuteAsync_WithEquivalentValues_ShouldNotSaveOrAudit()
    {
        var restaurant = CreateRestaurant();
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetTrackedOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        repository.Setup(item => item.GetOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        var auditService = new Mock<IAuditLogService>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = CreateUseCase(repository, new Mock<IMediaRepository>(), auditService, unitOfWork);

        await useCase.ExecuteAsync(
            new UpdateRestaurantRequestDto { Name = " Bún Bò Bà Diệu " },
            CancellationToken.None);

        auditService.Verify(item => item.Add(It.IsAny<AuditLogEntry>()), Times.Never);
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Reorder existing gallery rows without replacing entities that have the same composite keys.
    /// <summary>Verifies gallery reordering updates sort order safely for EF-tracked rows.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenGalleryIsReordered_ShouldReuseExistingImageRows()
    {
        var restaurant = CreateRestaurant();
        var firstImage = restaurant.Images.Single();
        var secondImage = new RestaurantImage
        {
            MediaId = 102,
            SortOrder = 1,
            Media = new MediaFile { Id = 102, Url = "https://cdn.test/second.jpg" }
        };
        restaurant.Images.Add(secondImage);
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetTrackedOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        repository.Setup(item => item.GetOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(item => item.GetOwnedByIdsAsync(12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MediaFile { Id = 101, MimeType = "image/jpeg", Url = "https://cdn.test/old.jpg" },
                new MediaFile { Id = 102, MimeType = "image/png", Url = "https://cdn.test/second.jpg" }
            ]);
        var useCase = CreateUseCase(
            repository,
            mediaRepository,
            new Mock<IAuditLogService>(),
            new Mock<IUnitOfWork>());

        await useCase.ExecuteAsync(
            new UpdateRestaurantRequestDto { ImageMediaIds = [102, 101] },
            CancellationToken.None);

        restaurant.Images.Should().HaveCount(2);
        restaurant.Images.Should().Contain(firstImage);
        restaurant.Images.Should().Contain(secondImage);
        restaurant.Images.OrderBy(item => item.SortOrder).Select(item => item.MediaId).Should().Equal(102, 101);
    }

    // TEST-05: Reject an owned PDF when it is used as the restaurant cover.
    /// <summary>Verifies that ownership alone is insufficient for image-only fields.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOwnedPdfAsCover_ShouldThrowValidationException()
    {
        var restaurant = CreateRestaurant();
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetTrackedOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(item => item.GetOwnedByIdsAsync(12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaFile { Id = 9001, UploaderId = 12, MimeType = "application/pdf" }]);
        var useCase = CreateUseCase(
            repository,
            mediaRepository,
            new Mock<IAuditLogService>(),
            new Mock<IUnitOfWork>());

        var act = () => useCase.ExecuteAsync(
            new UpdateRestaurantRequestDto { CoverMediaId = 9001 },
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be(nameof(UpdateRestaurantRequestDto.CoverMediaId));
    }

    // TEST-06: Reject an owned PDF when it is used in the restaurant gallery.
    /// <summary>Verifies an owned document cannot be inserted into the restaurant gallery.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOwnedPdfInGallery_ShouldThrowValidationException()
    {
        var restaurant = CreateRestaurant();
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetTrackedOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(item => item.GetOwnedByIdsAsync(12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaFile { Id = 9001, UploaderId = 12, MimeType = "application/pdf" }]);
        var useCase = CreateUseCase(
            repository,
            mediaRepository,
            new Mock<IAuditLogService>(),
            new Mock<IUnitOfWork>());

        var act = () => useCase.ExecuteAsync(
            new UpdateRestaurantRequestDto { ImageMediaIds = [9001] },
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be(nameof(UpdateRestaurantRequestDto.ImageMediaIds));
    }

    // TEST-07: Accept supported image MIME values regardless of their casing.
    /// <summary>Verifies compatibility with legacy media rows whose MIME casing was not normalized.</summary>
    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("IMAGE/JPEG")]
    [InlineData("Image/Png")]
    [InlineData("IMAGE/WEBP")]
    public async Task ExecuteAsync_WithSupportedImageMimeType_ShouldUpdateCover(string mimeType)
    {
        var restaurant = CreateRestaurant();
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetTrackedOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(item => item.GetOwnedByIdsAsync(12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaFile
            {
                Id = 102,
                UploaderId = 12,
                MimeType = mimeType,
                Url = "https://cdn.test/cover.jpg"
            }]);
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = CreateUseCase(
            repository,
            mediaRepository,
            new Mock<IAuditLogService>(),
            unitOfWork);

        var result = await useCase.ExecuteAsync(
            new UpdateRestaurantRequestDto { CoverMediaId = 102 },
            CancellationToken.None);

        restaurant.CoverMediaId.Should().Be(102);
        result.CoverUrl.Should().Be("https://cdn.test/cover.jpg");
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-08: Normalize surrounding whitespace before persisting scalar values.
    /// <summary>Verifies required and optional text normalization in OWN-03.</summary>
    [Fact]
    public async Task ExecuteAsync_WithSurroundingWhitespace_ShouldPersistNormalizedValues()
    {
        var restaurant = CreateRestaurant();
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetTrackedOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        var useCase = CreateUseCase(
            repository,
            new Mock<IMediaRepository>(),
            new Mock<IAuditLogService>(),
            new Mock<IUnitOfWork>());

        await useCase.ExecuteAsync(
            new UpdateRestaurantRequestDto
            {
                Name = " Tên mới ",
                Description = " Mô tả mới ",
                Phone = " 0901234567 ",
                Email = " OWNER@EXAMPLE.COM ",
                Website = " https://example.com ",
                AddressLine = " 34 Lê Lợi ",
                Ward = " Phường 1 ",
                District = " Quận 1 ",
                ProvinceName = " Thành phố Hồ Chí Minh "
            },
            CancellationToken.None);

        restaurant.Name.Should().Be("Tên mới");
        restaurant.Description.Should().Be("Mô tả mới");
        restaurant.Phone.Should().Be("0901234567");
        restaurant.Email.Should().Be("owner@example.com");
        restaurant.Website.Should().Be("https://example.com");
        restaurant.AddressLine.Should().Be("34 Lê Lợi");
        restaurant.Ward.Should().Be("Phường 1");
        restaurant.District.Should().Be("Quận 1");
        restaurant.ProvinceName.Should().Be("Thành phố Hồ Chí Minh");
    }

    /// <summary>Creates OWN-03 with an authenticated owner and default response dependencies.</summary>
    private static UpdateOwnerRestaurantUseCase CreateUseCase(
        Mock<IRestaurantRepository> repository,
        Mock<IMediaRepository> mediaRepository,
        Mock<IAuditLogService> auditService,
        Mock<IUnitOfWork> unitOfWork)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(12);
        return new UpdateOwnerRestaurantUseCase(
            new UpdateRestaurantRequestDtoValidator(),
            repository.Object,
            mediaRepository.Object,
            currentUser.Object,
            auditService.Object,
            new Mock<ICloudinaryService>().Object,
            unitOfWork.Object,
            TimeProvider.System);
    }

    /// <summary>Creates an existing tracked restaurant with one gallery image.</summary>
    private static Domain.Entities.Restaurant CreateRestaurant()
    {
        var restaurant = new Domain.Entities.Restaurant
        {
            Id = 7,
            OwnerId = 12,
            Name = "Bún Bò Bà Diệu",
            Slug = "bun-bo-ba-dieu",
            AddressLine = "12 Lê Duẩn",
            District = "Hải Châu",
            ProvinceName = "Thành phố Đà Nẵng",
            Latitude = 16.067812m,
            Longitude = 108.220116m
        };
        restaurant.Images.Add(new RestaurantImage
        {
            MediaId = 101,
            SortOrder = 0,
            Media = new MediaFile { Id = 101, Url = "https://cdn.test/old.jpg" }
        });
        return restaurant;
    }
}
