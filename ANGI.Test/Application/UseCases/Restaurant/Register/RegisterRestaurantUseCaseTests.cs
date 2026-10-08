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

namespace ANGI.Test.Application.UseCases.Restaurant.Register;

public sealed class RegisterRestaurantUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc);

    // TEST-01: Create the restaurant aggregate with initial statuses, images, and business hours.
    /// <summary>Verifies the complete successful OWN-01 registration workflow.</summary>
    [Fact]
    public async Task ExecuteAsync_WithValidRequest_ShouldCreateRestaurantAggregate()
    {
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
                new MediaFile { Id = 100, UploaderId = 12, Url = "https://cdn.test/cover.jpg" },
                new MediaFile { Id = 101, UploaderId = 12, Url = "https://cdn.test/gallery.jpg" }
            ]);
        var unitOfWork = new Mock<IUnitOfWork>();
        var transaction = new Mock<IUnitOfWorkTransaction>();
        unitOfWork.Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                addedRestaurant!.Id = 7;
                addedRestaurant.CreatedAt = Now;
            })
            .ReturnsAsync(3);
        var useCase = CreateUseCase(restaurantRepository, mediaRepository, unitOfWork, transaction);

        var result = await useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        result.Id.Should().Be(7);
        result.Slug.Should().Be("bun-bo-ba-dieu");
        result.VerificationStatus.Should().Be("unverified");
        result.OperatingStatus.Should().Be("open");
        result.ModerationStatus.Should().Be("visible");
        result.CoverUrl.Should().Be("https://cdn.test/cover.jpg");
        result.Images.Should().ContainSingle().Which.Should().Be("https://cdn.test/gallery.jpg");
        result.BusinessHours.Should().ContainSingle();
        result.IsOpenNow.Should().BeTrue();
        addedRestaurant!.OwnerId.Should().Be(12);
        addedRestaurant.VerificationStatus.Should().Be(RestaurantVerificationStatus.Unverified);
        addedRestaurant.Images.Should().ContainSingle();
        addedRestaurant.BusinessHours.Should().ContainSingle();
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
        var restaurantRepository = new Mock<IRestaurantRepository>();
        restaurantRepository.Setup(repo => repo.ExistsByOwnerIdAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = CreateUseCase(
            restaurantRepository,
            new Mock<IMediaRepository>(),
            unitOfWork);

        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("OWNER_ALREADY_HAS_RESTAURANT");
        restaurantRepository.Verify(repo => repo.Add(It.IsAny<Domain.Entities.Restaurant>()), Times.Never);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Reject media references that do not belong to the authenticated owner.
    /// <summary>Verifies media ownership before creating a restaurant aggregate.</summary>
    [Fact]
    public async Task ExecuteAsync_WithMissingOwnedMedia_ShouldThrowValidationException()
    {
        var restaurantRepository = new Mock<IRestaurantRepository>();
        restaurantRepository.Setup(repo => repo.ExistsByOwnerIdAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(repo => repo.GetOwnedByIdsAsync(
                12,
                It.IsAny<IReadOnlyCollection<long>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MediaFile>());
        var useCase = CreateUseCase(
            restaurantRepository,
            mediaRepository,
            new Mock<IUnitOfWork>());

        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        restaurantRepository.Verify(repo => repo.Add(It.IsAny<Domain.Entities.Restaurant>()), Times.Never);
    }

    // TEST-04: Validate required coordinates before querying repositories.
    /// <summary>Verifies first-statement validation for required numeric coordinates.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCoordinates_ShouldThrowValidationExceptionFirst()
    {
        var restaurantRepository = new Mock<IRestaurantRepository>();
        var useCase = CreateUseCase(
            restaurantRepository,
            new Mock<IMediaRepository>(),
            new Mock<IUnitOfWork>());
        var request = ValidRequest();
        request.Latitude = null;

        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        restaurantRepository.Verify(
            repo => repo.ExistsByOwnerIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
        District = "Hải Châu",
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
