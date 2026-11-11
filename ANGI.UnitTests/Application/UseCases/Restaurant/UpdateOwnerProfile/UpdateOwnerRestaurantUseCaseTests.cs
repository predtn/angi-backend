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

namespace ANGI.UnitTests.Application.UseCases.Restaurant.UpdateOwnerProfile;

/// <summary>Verifies UpdateOwnerRestaurantUseCase updates the owner's restaurant profile and gallery and audits real changes only.</summary>
public sealed class UpdateOwnerRestaurantUseCaseTests
{
    // TEST-01: Update supplied fields, replace the gallery, and audit only changed values.
    /// <summary>Verifies the successful atomic OWN-03 update workflow.</summary>
    [Fact]
    public async Task ExecuteAsync_WithChangedFields_ShouldUpdateAndWriteAuditLog()
    {
        // STEP-01: Create restaurant through CreateRestaurant.
        var restaurant = CreateRestaurant();
        // STEP-02: Declare audit to capture a call argument; mock IRestaurantRepository and 3 more; stub repository.GetTrackedOwnerProfileAsync and 2 more.
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
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, mediaRepository, auditService, unitOfWork);
        // STEP-04: Create request as UpdateRestaurantRequestDto.
        var request = new UpdateRestaurantRequestDto
        {
            Name = " Bún Bò Bà Diệu - Lê Duẩn ",
            ImageMediaIds = [102]
        };

        // STEP-05: Run useCase.ExecuteAsync with request and keep result.
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // STEP-06: Assert restaurant.Name is the expected text and restaurant.Images.Select(filtered) equals 102.
        restaurant.Name.Should().Be("Bún Bò Bà Diệu - Lê Duẩn");
        restaurant.Images.Select(item => item.MediaId).Should().Equal(102);
        // STEP-07: Assert result.Images equals "https://cdn.test/new.jpg" and result.ImageMediaIds equals 102.
        result.Images.Should().Equal("https://cdn.test/new.jpg");
        result.ImageMediaIds.Should().Equal(102);
        // STEP-08: Assert audit is not null, audit.Action is AuditActions.RestaurantUpdated and audit.EntityType is AuditEntityTypes.Restaurant.
        audit.Should().NotBeNull();
        audit!.Action.Should().Be(AuditActions.RestaurantUpdated);
        audit.EntityType.Should().Be(AuditEntityTypes.Restaurant);
        // STEP-09: Assert audit.OldValues.Keys is equivalent to "name".
        ((IDictionary<string, object?>)audit.OldValues!).Keys.Should().BeEquivalentTo("name", "image_media_ids");
        // STEP-10: Verify SaveChangesAsync is called once and GetOwnerProfileAsync is never called.
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.GetOwnerProfileAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-02: Reject gallery media that does not belong to the authenticated owner.
    /// <summary>Verifies media ownership before mutating or saving the restaurant.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnownedMedia_ShouldThrowValidationException()
    {
        // STEP-01: Create restaurant through CreateRestaurant.
        var restaurant = CreateRestaurant();
        // STEP-02: Mock IRestaurantRepository, IMediaRepository and 2 more; stub repository.GetTrackedOwnerProfileAsync and mediaRepository.GetOwnedByIdsAsync.
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetTrackedOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(item => item.GetOwnedByIdsAsync(12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MediaFile>());
        var auditService = new Mock<IAuditLogService>();
        var unitOfWork = new Mock<IUnitOfWork>();
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, mediaRepository, auditService, unitOfWork);

        // STEP-04: Wrap useCase.ExecuteAsync with UpdateRestaurantRequestDto { CoverMediaId = 999 } in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(
            new UpdateRestaurantRequestDto { CoverMediaId = 999 },
            CancellationToken.None);

        // STEP-05: Assert act throws ValidationException, exception.Errors has exactly one matching item and exception.Errors.PropertyName is "CoverMediaId".
        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be("CoverMediaId");
        // STEP-06: Verify Add is never called and SaveChangesAsync is never called.
        auditService.Verify(item => item.Add(It.IsAny<AuditLogEntry>()), Times.Never);
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Skip persistence and audit when supplied values normalize to current values.
    /// <summary>Verifies that an idempotent PATCH does not create a false change record.</summary>
    [Fact]
    public async Task ExecuteAsync_WithEquivalentValues_ShouldNotSaveOrAudit()
    {
        // STEP-01: Create restaurant through CreateRestaurant.
        var restaurant = CreateRestaurant();
        // STEP-02: Mock IRestaurantRepository, IAuditLogService and IUnitOfWork; stub repository.GetTrackedOwnerProfileAsync and repository.GetOwnerProfileAsync.
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetTrackedOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        repository.Setup(item => item.GetOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        var auditService = new Mock<IAuditLogService>();
        var unitOfWork = new Mock<IUnitOfWork>();
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, new Mock<IMediaRepository>(), auditService, unitOfWork);

        // STEP-04: Run useCase.ExecuteAsync with UpdateRestaurantRequestDto { Name = the expected text }.
        await useCase.ExecuteAsync(
            new UpdateRestaurantRequestDto { Name = " Bún Bò Bà Diệu " },
            CancellationToken.None);

        // STEP-05: Verify Add is never called and SaveChangesAsync is never called.
        auditService.Verify(item => item.Add(It.IsAny<AuditLogEntry>()), Times.Never);
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Reorder existing gallery rows without replacing entities that have the same composite keys.
    /// <summary>Verifies gallery reordering updates sort order safely for EF-tracked rows.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenGalleryIsReordered_ShouldReuseExistingImageRows()
    {
        // STEP-01: Create restaurant through CreateRestaurant, get firstImage from restaurant.Images.Single, create secondImage as RestaurantImage and 1 more.
        var restaurant = CreateRestaurant();
        var firstImage = restaurant.Images.Single();
        var secondImage = new RestaurantImage
        {
            MediaId = 102,
            SortOrder = 1,
            Media = new MediaFile { Id = 102, Url = "https://cdn.test/second.jpg" }
        };
        restaurant.Images.Add(secondImage);
        // STEP-02: Mock IRestaurantRepository and IMediaRepository; stub repository.GetTrackedOwnerProfileAsync, repository.GetOwnerProfileAsync and 1 more.
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
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            repository,
            mediaRepository,
            new Mock<IAuditLogService>(),
            new Mock<IUnitOfWork>());

        // STEP-04: Run useCase.ExecuteAsync with UpdateRestaurantRequestDto { ImageMediaIds = the expected items } and keep result.
        var result = await useCase.ExecuteAsync(
            new UpdateRestaurantRequestDto { ImageMediaIds = [102, 101] },
            CancellationToken.None);

        // STEP-05: Assert restaurant.Images has 2 item(s), restaurant.Images contains firstImage, restaurant.Images contains secondImage and 3 more.
        restaurant.Images.Should().HaveCount(2);
        restaurant.Images.Should().Contain(firstImage);
        restaurant.Images.Should().Contain(secondImage);
        restaurant.Images.OrderBy(item => item.SortOrder).Select(item => item.MediaId).Should().Equal(102, 101);
        result.ImageMediaIds.Should().Equal(102, 101);
        result.Images.Should().Equal("https://cdn.test/second.jpg", "https://cdn.test/old.jpg");
    }

    // TEST-05: Reject an owned PDF when it is used as the restaurant cover.
    /// <summary>Verifies that ownership alone is insufficient for image-only fields.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOwnedPdfAsCover_ShouldThrowValidationException()
    {
        // STEP-01: Create restaurant through CreateRestaurant.
        var restaurant = CreateRestaurant();
        // STEP-02: Mock IRestaurantRepository and IMediaRepository; stub repository.GetTrackedOwnerProfileAsync and mediaRepository.GetOwnedByIdsAsync.
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetTrackedOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(item => item.GetOwnedByIdsAsync(12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaFile { Id = 9001, UploaderId = 12, MimeType = "application/pdf" }]);
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            repository,
            mediaRepository,
            new Mock<IAuditLogService>(),
            new Mock<IUnitOfWork>());

        // STEP-04: Wrap useCase.ExecuteAsync with UpdateRestaurantRequestDto { CoverMediaId = 9001 } in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(
            new UpdateRestaurantRequestDto { CoverMediaId = 9001 },
            CancellationToken.None);

        // STEP-05: Assert act throws ValidationException, exception.Errors has exactly one matching item and exception.Errors.PropertyName is nameof().
        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be(nameof(UpdateRestaurantRequestDto.CoverMediaId));
    }

    // TEST-06: Reject an owned PDF when it is used in the restaurant gallery.
    /// <summary>Verifies an owned document cannot be inserted into the restaurant gallery.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOwnedPdfInGallery_ShouldThrowValidationException()
    {
        // STEP-01: Create restaurant through CreateRestaurant.
        var restaurant = CreateRestaurant();
        // STEP-02: Mock IRestaurantRepository and IMediaRepository; stub repository.GetTrackedOwnerProfileAsync and mediaRepository.GetOwnedByIdsAsync.
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetTrackedOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(item => item.GetOwnedByIdsAsync(12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaFile { Id = 9001, UploaderId = 12, MimeType = "application/pdf" }]);
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            repository,
            mediaRepository,
            new Mock<IAuditLogService>(),
            new Mock<IUnitOfWork>());

        // STEP-04: Wrap useCase.ExecuteAsync with UpdateRestaurantRequestDto { ImageMediaIds = the expected items } in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(
            new UpdateRestaurantRequestDto { ImageMediaIds = [9001] },
            CancellationToken.None);

        // STEP-05: Assert act throws ValidationException, exception.Errors has exactly one matching item and exception.Errors.PropertyName is nameof().
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
        // STEP-01: Create restaurant through CreateRestaurant.
        var restaurant = CreateRestaurant();
        // STEP-02: Mock IRestaurantRepository, IMediaRepository and IUnitOfWork; stub repository.GetTrackedOwnerProfileAsync and mediaRepository.GetOwnedByIdsAsync.
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
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            repository,
            mediaRepository,
            new Mock<IAuditLogService>(),
            unitOfWork);

        // STEP-04: Run useCase.ExecuteAsync with UpdateRestaurantRequestDto { CoverMediaId = 102 } and keep result.
        var result = await useCase.ExecuteAsync(
            new UpdateRestaurantRequestDto { CoverMediaId = 102 },
            CancellationToken.None);

        // STEP-05: Assert restaurant.CoverMediaId is 102 and result.CoverUrl is "https://cdn.test/cover.jpg".
        restaurant.CoverMediaId.Should().Be(102);
        result.CoverUrl.Should().Be("https://cdn.test/cover.jpg");
        // STEP-06: Verify SaveChangesAsync is called once.
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-08: Normalize surrounding whitespace before persisting scalar values.
    /// <summary>Verifies required and optional text normalization in OWN-03.</summary>
    [Fact]
    public async Task ExecuteAsync_WithSurroundingWhitespace_ShouldPersistNormalizedValues()
    {
        // STEP-01: Create restaurant through CreateRestaurant.
        var restaurant = CreateRestaurant();
        // STEP-02: Mock IRestaurantRepository; stub repository.GetTrackedOwnerProfileAsync.
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetTrackedOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            repository,
            new Mock<IMediaRepository>(),
            new Mock<IAuditLogService>(),
            new Mock<IUnitOfWork>());

        // STEP-04: Run useCase.ExecuteAsync with UpdateRestaurantRequestDto { Name, Description, Phone, ... }.
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
                ProvinceName = " Thành phố Hồ Chí Minh "
            },
            CancellationToken.None);

        // STEP-05: Assert restaurant.Name is the expected text, restaurant.Description is the expected text, restaurant.Phone is "0901234567" and 5 more.
        restaurant.Name.Should().Be("Tên mới");
        restaurant.Description.Should().Be("Mô tả mới");
        restaurant.Phone.Should().Be("0901234567");
        restaurant.Email.Should().Be("owner@example.com");
        restaurant.Website.Should().Be("https://example.com");
        restaurant.AddressLine.Should().Be("34 Lê Lợi");
        restaurant.Ward.Should().Be("Phường 1");
        restaurant.ProvinceName.Should().Be("Thành phố Hồ Chí Minh");
    }

    // TEST-09: Reject invalid replacement phones before querying, mutating, auditing, or saving the restaurant.
    /// <summary>Proves an invalid contact cannot enter persistence through the partial update workflow.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234567890")]
    [InlineData("0123456789")]
    [InlineData("090123456")]
    [InlineData("09012345678")]
    [InlineData("+840901234567")]
    public async Task ExecuteAsync_WithInvalidPhone_ShouldRejectBeforeAnySideEffect(string? phone)
    {
        // STEP-01: Mock IRestaurantRepository, IMediaRepository, IAuditLogService and IUnitOfWork.
        var repository = new Mock<IRestaurantRepository>(MockBehavior.Strict);
        var media = new Mock<IMediaRepository>(MockBehavior.Strict);
        var audit = new Mock<IAuditLogService>(MockBehavior.Strict);
        var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, media, audit, unitOfWork);

        // STEP-03: Wrap useCase.ExecuteAsync with UpdateRestaurantRequestDto { Phone = phone } in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(new UpdateRestaurantRequestDto { Phone = phone }, CancellationToken.None);

        // STEP-04: Assert act throws ValidationException, exception.Errors has exactly one matching item and exception.Errors.PropertyName is "Phone".
        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.Errors.Should().ContainSingle().Which.PropertyName.Should().Be("Phone");
        // STEP-05: Verify repository receives no other calls, media receives no other calls, audit receives no other calls and unitOfWork receives no other calls.
        repository.VerifyNoOtherCalls();
        media.VerifyNoOtherCalls();
        audit.VerifyNoOtherCalls();
        unitOfWork.VerifyNoOtherCalls();
    }

    /// <summary>Creates OWN-03 with an authenticated owner and default response dependencies.</summary>
    // TEST-10: Keep one saved photo, drop another and append a new upload in a single list sent back from OWN-02.
    /// <summary>Verifies partial gallery edits built from the imageMediaIds returned by OWN-02.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenGalleryKeepsDropsAndAddsPhotos_ShouldApplyTheSentOrder()
    {
        // STEP-01: Create restaurant through CreateRestaurant, get keptImage from restaurant.Images.Single and add a RestaurantImage to restaurant.Images.
        var restaurant = CreateRestaurant();
        var keptImage = restaurant.Images.Single();
        restaurant.Images.Add(new RestaurantImage
        {
            MediaId = 102,
            SortOrder = 1,
            Media = new MediaFile { Id = 102, Url = "https://cdn.test/dropped.jpg" }
        });
        // STEP-02: Declare audit to capture a call argument; mock IRestaurantRepository and 2 more; stub repository.GetTrackedOwnerProfileAsync and 2 more.
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetTrackedOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(item => item.GetOwnedByIdsAsync(12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MediaFile { Id = 101, MimeType = "image/jpeg", Url = "https://cdn.test/old.jpg" },
                new MediaFile { Id = 103, MimeType = "image/webp", Url = "https://cdn.test/added.webp" }
            ]);
        var auditService = new Mock<IAuditLogService>();
        AuditLogEntry? audit = null;
        auditService.Setup(item => item.Add(It.IsAny<AuditLogEntry>()))
            .Callback<AuditLogEntry>(entry => audit = entry);
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, mediaRepository, auditService, new Mock<IUnitOfWork>());

        // STEP-04: Run useCase.ExecuteAsync with UpdateRestaurantRequestDto { ImageMediaIds = the expected items } and keep result.
        var result = await useCase.ExecuteAsync(
            new UpdateRestaurantRequestDto { ImageMediaIds = [103, 101] },
            CancellationToken.None);

        // STEP-05: Assert restaurant.Images has 2 item(s) and Images.OrderBy(filtered).Select(filtered) equals 103.
        restaurant.Images.Should().HaveCount(2).And.Contain(keptImage);
        restaurant.Images.OrderBy(item => item.SortOrder).Select(item => item.MediaId).Should().Equal(103, 101);
        // STEP-06: Assert result.ImageMediaIds equals 103 and result.Images equals "https://cdn.test/added.webp".
        result.ImageMediaIds.Should().Equal(103, 101);
        result.Images.Should().Equal("https://cdn.test/added.webp", "https://cdn.test/old.jpg");
        // STEP-07: Assert audit is not null, audit.OldValues["image_media_ids"] is equivalent to the expected items and 1 more.
        audit.Should().NotBeNull();
        ((IDictionary<string, object?>)audit!.OldValues!)["image_media_ids"].Should().BeEquivalentTo(new[] { 101L, 102L }, options => options.WithStrictOrdering());
        ((IDictionary<string, object?>)audit.NewValues!)["image_media_ids"].Should().BeEquivalentTo(new[] { 103L, 101L }, options => options.WithStrictOrdering());
    }

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
