using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.Register;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.Register;

/// <summary>Verifies RegisterRestaurantUseCase creates one restaurant per owner with owned media and valid business hours.</summary>
public sealed class RegisterRestaurantUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc);

    // TEST-01: Create the restaurant aggregate with initial statuses, images, and business hours.
    /// <summary>Verifies the complete successful OWN-01 registration workflow.</summary>
    [Fact]
    public async Task ExecuteAsync_WithValidRequest_ShouldCreateRestaurantAggregate()
    {
        // STEP-01: Declare addedRestaurant to capture a call argument; mock IRestaurantRepository and 3 more; stub restaurantRepository.ExistsByOwnerIdAsync and 4 more.
        var restaurantRepository = new Mock<IRestaurantRepository>();
        restaurantRepository.Setup(repo => repo.ExistsByOwnerIdAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        restaurantRepository.Setup(repo => repo.SlugExistsAsync("bun-bo-ba-dieu", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        Domain.Entities.Restaurant? addedRestaurant = null;
        restaurantRepository.Setup(repo => repo.Add(It.IsAny<Domain.Entities.Restaurant>()))
            .Callback<Domain.Entities.Restaurant>(restaurant => addedRestaurant = restaurant);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(repo => repo.GetOwnedByIdsAsync(
                12,
                It.IsAny<IReadOnlyCollection<long>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new MediaFile { Id = 100, UploaderId = 12, MimeType = "image/jpeg", Url = "https://cdn.test/cover.jpg" },
                new MediaFile { Id = 101, UploaderId = 12, MimeType = "image/png", Url = "https://cdn.test/gallery.jpg" }
            ]);
        var unitOfWork = new Mock<IUnitOfWork>();
        var transaction = new Mock<IUnitOfWorkTransaction>();
        unitOfWork.Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                addedRestaurant!.Id = 7;
                addedRestaurant.CreatedAt = Now;
                addedRestaurant.UpdatedAt = Now;
            })
            .ReturnsAsync(3);
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(restaurantRepository, mediaRepository, unitOfWork, transaction);

        // STEP-03: Run useCase.ExecuteAsync and keep result.
        var result = await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        // STEP-04: Assert result.Id is 7, result.Slug is "bun-bo-ba-dieu", result.VerificationStatus is "unverified", result.OperatingStatus is "open" and 9 more.
        result.Id.Should().Be(7);
        result.Slug.Should().Be("bun-bo-ba-dieu");
        result.VerificationStatus.Should().Be("unverified");
        result.OperatingStatus.Should().Be("open");
        result.ModerationStatus.Should().Be("visible");
        result.CoverUrl.Should().Be("https://cdn.test/cover.jpg");
        result.Images.Should().ContainSingle().Which.Should().Be("https://cdn.test/gallery.jpg");
        result.ImageMediaIds.Should().Equal(101);
        result.BusinessHours.Should().ContainSingle();
        result.IsOpenNow.Should().BeTrue();
        result.CreatedAt.Should().Be(Now);
        result.UpdatedAt.Should().Be(Now);
        // STEP-05: Assert addedRestaurant.OwnerId is 12, addedRestaurant.VerificationStatus is Unverified and 2 more.
        addedRestaurant!.OwnerId.Should().Be(12);
        addedRestaurant.VerificationStatus.Should().Be(RestaurantVerificationStatus.Unverified);
        addedRestaurant.Images.Should().ContainSingle();
        addedRestaurant.BusinessHours.Should().ContainSingle();
        // STEP-06: Verify LockOwnerForRegistrationAsync is called once, SaveChangesAsync is called once and CommitAsync is called once.
        restaurantRepository.Verify(
            repo => repo.LockOwnerForRegistrationAsync(12, It.IsAny<CancellationToken>()),
            Times.Once);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Reject an owner who already has a restaurant without saving changes.
    /// <summary>Verifies the documented OWNER_ALREADY_HAS_RESTAURANT conflict.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenOwnerAlreadyHasRestaurant_ShouldThrowConflict()
    {
        // STEP-01: Mock IRestaurantRepository and IUnitOfWork; stub restaurantRepository.ExistsByOwnerIdAsync.
        var restaurantRepository = new Mock<IRestaurantRepository>();
        restaurantRepository.Setup(repo => repo.ExistsByOwnerIdAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var unitOfWork = new Mock<IUnitOfWork>();
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            restaurantRepository,
            new Mock<IMediaRepository>(),
            unitOfWork);

        // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        // STEP-04: Assert act throws ConflictException and exception.ErrorCode is "OWNER_ALREADY_HAS_RESTAURANT".
        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("OWNER_ALREADY_HAS_RESTAURANT");
        // STEP-05: Verify Add is never called and SaveChangesAsync is never called.
        restaurantRepository.Verify(repo => repo.Add(It.IsAny<Domain.Entities.Restaurant>()), Times.Never);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Reject media references that do not belong to the authenticated owner, on the field that holds each id.
    /// <summary>Verifies media ownership before creating a restaurant aggregate.</summary>
    [Fact]
    public async Task ExecuteAsync_WithMissingOwnedMedia_ShouldThrowValidationException()
    {
        // STEP-01: Mock IRestaurantRepository and IMediaRepository; stub restaurantRepository.ExistsByOwnerIdAsync and mediaRepository.GetOwnedByIdsAsync.
        var restaurantRepository = new Mock<IRestaurantRepository>();
        restaurantRepository.Setup(repo => repo.ExistsByOwnerIdAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(repo => repo.GetOwnedByIdsAsync(
                12,
                It.IsAny<IReadOnlyCollection<long>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MediaFile>());
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            restaurantRepository,
            mediaRepository,
            new Mock<IUnitOfWork>());

        // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        // STEP-04: Assert act throws ValidationException and exception.Errors.Select(filtered) is equivalent to "CoverMediaId".
        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.Errors.Select(error => error.PropertyName)
            .Should().BeEquivalentTo("CoverMediaId", "ImageMediaIds");
        // STEP-05: Verify Add is never called.
        restaurantRepository.Verify(repo => repo.Add(It.IsAny<Domain.Entities.Restaurant>()), Times.Never);
    }

    // TEST-04: Report a missing cover image on coverMediaId only when the gallery images exist.
    /// <summary>Verifies that the frontend can attach the error to the cover field.</summary>
    [Fact]
    public async Task ExecuteAsync_WithMissingCoverOnly_ShouldReportCoverMediaId()
    {
        // STEP-01: Mock IRestaurantRepository and IMediaRepository; stub restaurantRepository.ExistsByOwnerIdAsync and mediaRepository.GetOwnedByIdsAsync.
        var restaurantRepository = new Mock<IRestaurantRepository>();
        restaurantRepository.Setup(repo => repo.ExistsByOwnerIdAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(repo => repo.GetOwnedByIdsAsync(
                12,
                It.IsAny<IReadOnlyCollection<long>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaFile { Id = 101, UploaderId = 12, MimeType = "image/png", Url = "https://cdn.test/gallery.jpg" }]);
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(restaurantRepository, mediaRepository, new Mock<IUnitOfWork>());

        // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        // STEP-04: Assert act throws ValidationException, exception.Errors has exactly one matching item and exception.Errors.PropertyName is "CoverMediaId".
        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be("CoverMediaId");
    }

    // TEST-05: Validate required coordinates before querying repositories.
    /// <summary>Verifies first-statement validation for required numeric coordinates.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCoordinates_ShouldThrowValidationExceptionFirst()
    {
        // STEP-01: Mock IRestaurantRepository.
        var restaurantRepository = new Mock<IRestaurantRepository>();
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            restaurantRepository,
            new Mock<IMediaRepository>(),
            new Mock<IUnitOfWork>());
        // STEP-03: Create request through ValidRequest and set request.Latitude to null.
        var request = ValidRequest();
        request.Latitude = null;

        // STEP-04: Wrap useCase.ExecuteAsync with request in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // STEP-05: Assert act throws ValidationException.
        await act.Should().ThrowAsync<ValidationException>();
        // STEP-06: Verify ExistsByOwnerIdAsync is never called.
        restaurantRepository.Verify(
            repo => repo.ExistsByOwnerIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // TEST-06: Reject invalid phone input before opening a transaction or calling any persistence dependency.
    /// <summary>Proves phone validation stops registration before owner locking, media access, or writes.</summary>
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
        // STEP-01: Mock IRestaurantRepository, IMediaRepository, IUnitOfWork and ICurrentUserService.
        var repository = new Mock<IRestaurantRepository>(MockBehavior.Strict);
        var media = new Mock<IMediaRepository>(MockBehavior.Strict);
        var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Strict);
        // STEP-02: Create the RegisterRestaurantUseCase under test as useCase.
        var useCase = new RegisterRestaurantUseCase(
            new RegisterRestaurantRequestDtoValidator(), repository.Object, media.Object,
            currentUser.Object, unitOfWork.Object, new FixedTimeProvider(Now));
        // STEP-03: Create request through ValidRequest and set request.Phone to phone.
        var request = ValidRequest();
        request.Phone = phone!;

        // STEP-04: Wrap useCase.ExecuteAsync with request in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // STEP-05: Assert act throws ValidationException, exception.Errors has exactly one matching item and exception.Errors.PropertyName is "Phone".
        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.Errors.Should().ContainSingle().Which.PropertyName.Should().Be("Phone");
        // STEP-06: Verify repository receives no other calls, media receives no other calls, currentUser receives no other calls and unitOfWork receives no other calls.
        repository.VerifyNoOtherCalls();
        media.VerifyNoOtherCalls();
        currentUser.VerifyNoOtherCalls();
        unitOfWork.VerifyNoOtherCalls();
    }

    // TEST-07: Persist supported phone formatting while trimming surrounding whitespace.
    /// <summary>Verifies accepted mobile and landline contacts are returned exactly as persisted.</summary>
    [Theory]
    [InlineData(" 0901234567 ", "0901234567")]
    [InlineData("0236 3812 345", "0236 3812 345")]
    [InlineData("+84 90 123 4567", "+84 90 123 4567")]
    public async Task ExecuteAsync_WithValidPhone_ShouldPersistAndReturnContact(string phone, string expected)
    {
        // STEP-01: Mock IRestaurantRepository, IUnitOfWork and IUnitOfWorkTransaction.
        var repository = new Mock<IRestaurantRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var transaction = new Mock<IUnitOfWorkTransaction>();
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, new Mock<IMediaRepository>(), unitOfWork, transaction);
        // STEP-03: Create request through ValidRequest, set request.Phone to phone, set request.CoverMediaId to null and set request.ImageMediaIds to null.
        var request = ValidRequest();
        request.Phone = phone;
        request.CoverMediaId = null;
        request.ImageMediaIds = null;

        // STEP-04: Run useCase.ExecuteAsync with request and keep result.
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // STEP-05: Assert result.Phone is expected.
        result.Phone.Should().Be(expected);
        // STEP-06: Verify Add is called once, SaveChangesAsync is called once and CommitAsync is called once.
        repository.Verify(item => item.Add(It.Is<Domain.Entities.Restaurant>(value => value.Phone == expected)), Times.Once);
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-08: Accept supported restaurant image types and reject owned documents or unknown MIME types on each field.
    /// <summary>Verifies registration uses the same image-only boundary as profile updates before saving or committing.</summary>
    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", true)]
    [InlineData("image/webp", true)]
    [InlineData(" IMAGE/JPEG ", true)]
    [InlineData("application/pdf", false)]
    [InlineData("image/gif", false)]
    [InlineData("image/svg+xml", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task ExecuteAsync_WithOwnedMediaType_ShouldEnforceImageBoundary(string? mimeType, bool expectedValid)
    {
        // STEP-01: Assert for each isCover, act does not throw and 3 more; verify SaveChangesAsync is called once and 4 more.
        foreach (var isCover in new[] { true, false })
        {
            var repository = new Mock<IRestaurantRepository>();
            var media = new Mock<IMediaRepository>();
            media.Setup(item => item.GetOwnedByIdsAsync(12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([new MediaFile { Id = 100, UploaderId = 12, MimeType = mimeType!, Url = "https://cdn.test/image" }]);
            var unitOfWork = new Mock<IUnitOfWork>();
            var transaction = new Mock<IUnitOfWorkTransaction>();
            var useCase = CreateUseCase(repository, media, unitOfWork, transaction);
            var request = ValidRequest();
            request.CoverMediaId = isCover ? 100 : null;
            request.ImageMediaIds = isCover ? null : [100];

            var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

            if (expectedValid)
            {
                await act.Should().NotThrowAsync();
                unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
                transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
            }
            else
            {
                var exception = await act.Should().ThrowAsync<ValidationException>();
                exception.Which.Errors.Should().ContainSingle().Which.PropertyName
                    .Should().Be(isCover ? "CoverMediaId" : "ImageMediaIds");
                repository.Verify(item => item.Add(It.IsAny<Domain.Entities.Restaurant>()), Times.Never);
                unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
                transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
            }
        }
    }

    // TEST-09: Reject an unauthenticated caller after validation and before any transaction or repository access.
    /// <summary>Verifies the registration use case independently enforces the caller identity boundary.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutOwnerIdentity_ShouldThrowUnauthorizedBeforePersistence()
    {
        // STEP-01: Mock IRestaurantRepository, IMediaRepository and IUnitOfWork.
        var repository = new Mock<IRestaurantRepository>(MockBehavior.Strict);
        var media = new Mock<IMediaRepository>(MockBehavior.Strict);
        var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
        // STEP-02: Create the RegisterRestaurantUseCase under test as useCase.
        var useCase = new RegisterRestaurantUseCase(new RegisterRestaurantRequestDtoValidator(),
            repository.Object, media.Object, new Mock<ICurrentUserService>().Object,
            unitOfWork.Object, new FixedTimeProvider(Now));

        // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        // STEP-04: Assert act throws UnauthorizedException and the thrown exception.ErrorCode is "UNAUTHORIZED".
        (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
        // STEP-05: Verify repository receives no other calls, media receives no other calls and unitOfWork receives no other calls.
        repository.VerifyNoOtherCalls();
        media.VerifyNoOtherCalls();
        unitOfWork.VerifyNoOtherCalls();
    }

    // TEST-10: Normalize Vietnamese names and punctuation or fall back to a safe slug, then resolve existing suffixes.
    /// <summary>Verifies registration produces a readable unique slug even when the base name is already taken.</summary>
    [Theory]
    [InlineData("Đặng's Bún", "dang-s-bun")]
    [InlineData("🍜", "restaurant")]
    public async Task ExecuteAsync_WithCollidingSlug_ShouldUseNextAvailableSuffix(string name, string baseSlug)
    {
        // STEP-01: Mock IRestaurantRepository; stub repository.SlugExistsAsync.
        var repository = new Mock<IRestaurantRepository>();
        repository.SetupSequence(item => item.SlugExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true).ReturnsAsync(true).ReturnsAsync(false);
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, new Mock<IMediaRepository>(), new Mock<IUnitOfWork>());
        // STEP-03: Create request through ValidRequest, set request.Name to name, set request.CoverMediaId to null and set request.ImageMediaIds to null.
        var request = ValidRequest();
        request.Name = name;
        request.CoverMediaId = null;
        request.ImageMediaIds = null;

        // STEP-04: Run useCase.ExecuteAsync with request and keep result.
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // STEP-05: Assert result.Slug is baseSlug + "-3"; verify for each slug, SlugExistsAsync is called once.
        result.Slug.Should().Be(baseSlug + "-3");
        foreach (var slug in new[] { baseSlug, baseSlug + "-2", baseSlug + "-3" })
        {
            repository.Verify(item => item.SlugExistsAsync(slug, It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    // TEST-11: Dispose an uncommitted transaction and propagate a persistence failure without returning a successful registration.
    /// <summary>Verifies registration cannot report success or commit when its single save fails.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenSavingFails_ShouldDisposeWithoutCommit()
    {
        // STEP-01: Mock IRestaurantRepository, IUnitOfWork and IUnitOfWorkTransaction; stub unitOfWork.SaveChangesAsync.
        var repository = new Mock<IRestaurantRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Persistence failure"));
        var transaction = new Mock<IUnitOfWorkTransaction>();
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, new Mock<IMediaRepository>(), unitOfWork, transaction);
        // STEP-03: Create request through ValidRequest, set request.CoverMediaId to null and set request.ImageMediaIds to null.
        var request = ValidRequest();
        request.CoverMediaId = null;
        request.ImageMediaIds = null;

        // STEP-04: Wrap useCase.ExecuteAsync with request in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // STEP-05: Assert act throws InvalidOperationException.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Persistence failure");
        // STEP-06: Verify SaveChangesAsync is called once, CommitAsync is never called and DisposeAsync is called once.
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        transaction.Verify(item => item.DisposeAsync(), Times.Once);
    }

    /// <summary>Creates a registration use case with an authenticated owner and fixed clock.</summary>
    private static RegisterRestaurantUseCase CreateUseCase(
        Mock<IRestaurantRepository> restaurantRepository,
        Mock<IMediaRepository> mediaRepository,
        Mock<IUnitOfWork> unitOfWork,
        Mock<IUnitOfWorkTransaction>? transaction = null)
    {
        transaction ??= new Mock<IUnitOfWorkTransaction>();
        unitOfWork.Setup(work => work.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        restaurantRepository.Setup(repo => repo.LockOwnerForRegistrationAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(service => service.UserId).Returns(12);
        return new RegisterRestaurantUseCase(
            new RegisterRestaurantRequestDtoValidator(),
            restaurantRepository.Object,
            mediaRepository.Object,
            currentUser.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));
    }

    /// <summary>Creates a complete valid OWN-01 request.</summary>
    private static RegisterRestaurantRequestDto ValidRequest() => new()
    {
        Name = "Bún Bò Bà Diệu",
        Description = "Quán bún bò gia truyền",
        Phone = "02363812345",
        Email = "owner@restaurant.test",
        Website = "https://restaurant.test",
        AddressLine = "12 Lê Duẩn",
        Ward = "Hải Châu 1",
        ProvinceName = "Thành phố Đà Nẵng",
        Latitude = 16.067812m,
        Longitude = 108.220116m,
        PriceLevel = 1,
        CoverMediaId = 100,
        ImageMediaIds = [101],
        BusinessHours =
        [
            new BusinessHourDto { DayOfWeek = 3, OpenTime = "06:00", CloseTime = "13:30" }
        ]
    };

    /// <summary>Clock fixed at one instant so creation timestamps are deterministic.</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        /// <summary>Initializes a clock fixed at the supplied UTC instant.</summary>
        public FixedTimeProvider(DateTime now)
        {
            _now = new DateTimeOffset(now);
        }

        /// <summary>Returns the fixed instant for deterministic open-now calculations.</summary>
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
