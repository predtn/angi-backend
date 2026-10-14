using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.SubmitVerification;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.SubmitVerification;

public sealed class SubmitVerificationUseCaseTests
{
    private static readonly DateTime SubmittedAt = new(2026, 10, 17, 3, 0, 0, DateTimeKind.Utc);

    // TEST-01: Create a pending verification, attach owned private documents, update the restaurant, and return signed URLs.
    /// <summary>Verifies the complete successful OWN-06 transaction and response mapping.</summary>
    [Fact]
    public async Task ExecuteAsync_WithValidRequest_ShouldCreatePendingVerification()
    {
        var restaurant = new Domain.Entities.Restaurant
        {
            Id = 7,
            OwnerId = 12,
            VerificationStatus = RestaurantVerificationStatus.Unverified
        };
        var restaurantRepository = new Mock<IRestaurantRepository>();
        restaurantRepository.Setup(item => item.LockOwnerRestaurantForVerificationAsync(
                12,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        RestaurantVerification? addedVerification = null;
        restaurantRepository.Setup(item => item.AddVerification(It.IsAny<RestaurantVerification>()))
            .Callback<RestaurantVerification>(verification => addedVerification = verification);

        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(item => item.GetOwnedByIdsAsync(
                12,
                It.IsAny<IReadOnlyCollection<long>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                VerificationMedia(9001, "raw/authenticated/verification_doc/license.pdf"),
                VerificationMedia(9002, "image/authenticated/verification_doc/storefront.jpg")
            ]);

        var unitOfWork = new Mock<IUnitOfWork>();
        var transaction = new Mock<IUnitOfWorkTransaction>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        unitOfWork.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                addedVerification!.Id = 21;
                addedVerification.SubmittedAt = SubmittedAt;
            })
            .ReturnsAsync(4);

        var cloudinary = new Mock<ICloudinaryService>();
        cloudinary.Setup(item => item.GetUrl(It.IsAny<string>()))
            .Returns<string>(storageKey => $"https://signed.test/{Path.GetFileName(storageKey)}");
        var request = ValidRequest();
        var cancellationToken = new CancellationTokenSource().Token;
        var useCase = CreateUseCase(
            restaurantRepository,
            mediaRepository,
            unitOfWork,
            cloudinary,
            transaction: transaction);

        var result = await useCase.ExecuteAsync(request, cancellationToken);

        restaurant.VerificationStatus.Should().Be(RestaurantVerificationStatus.Pending);
        addedVerification.Should().NotBeNull();
        addedVerification!.RestaurantId.Should().Be(7);
        addedVerification.SubmittedBy.Should().Be(12);
        addedVerification.LegalName.Should().Be("Hộ kinh doanh Bún Bò Bà Diệu");
        addedVerification.BusinessLicenseNo.Should().Be("32A8012345");
        addedVerification.TaxCode.Should().Be("0401234567");
        addedVerification.OwnerNote.Should().Be("Bổ sung giấy ATTP");
        addedVerification.PreviousId.Should().BeNull();
        addedVerification.Status.Should().Be(VerificationStatus.Pending);
        addedVerification.Documents.Select(item => item.DocType)
            .Should().Equal(VerificationDocumentType.BusinessLicense, VerificationDocumentType.StorefrontPhoto);
        addedVerification.Documents.Select(item => item.MediaId).Should().Equal(9001, 9002);
        // The media rows come from an untracked query: a set navigation would make EF insert them again.
        addedVerification.Documents.Should().OnlyContain(item => item.Media == null);
        result.Id.Should().Be(21);
        result.RestaurantId.Should().Be(7);
        result.Status.Should().Be("pending");
        result.Documents.Select(item => item.DocType)
            .Should().Equal("business_license", "storefront_photo");
        result.Documents.Select(item => item.Url)
            .Should().Equal(
                "https://signed.test/license.pdf",
                "https://signed.test/storefront.jpg");
        result.SubmittedAt.Should().Be(SubmittedAt);
        result.PreviousId.Should().BeNull();
        result.ReviewedBy.Should().BeNull();
        unitOfWork.Verify(item => item.SaveChangesAsync(cancellationToken), Times.Once);
        transaction.Verify(item => item.CommitAsync(cancellationToken), Times.Once);
    }

    // TEST-02: Run request validation before opening a transaction or querying persistence.
    /// <summary>Verifies first-statement validation and the required business-license document.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutBusinessLicense_ShouldFailBeforePersistence()
    {
        var restaurantRepository = new Mock<IRestaurantRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var request = ValidRequest();
        request.Documents =
        [
            new VerificationDocumentRequestDto { DocType = "id_card", MediaId = 9002 }
        ];
        var useCase = CreateUseCase(
            restaurantRepository,
            new Mock<IMediaRepository>(),
            unitOfWork,
            new Mock<ICloudinaryService>());

        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        unitOfWork.Verify(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        restaurantRepository.Verify(item => item.LockOwnerRestaurantForVerificationAsync(
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Reject a missing authenticated user before opening the transaction.
    /// <summary>Verifies the standard UNAUTHORIZED guard for OWN-06.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutAuthenticatedUser_ShouldThrowUnauthorized()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = CreateUseCase(
            new Mock<IRestaurantRepository>(),
            new Mock<IMediaRepository>(),
            unitOfWork,
            new Mock<ICloudinaryService>(),
            ownerId: null);

        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("UNAUTHORIZED");
        unitOfWork.Verify(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Return RESTAURANT_NOT_FOUND when the authenticated owner has no restaurant.
    /// <summary>Verifies the ownership-scoped locked lookup and no-write behavior.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutRestaurant_ShouldThrowNotFound()
    {
        var restaurantRepository = new Mock<IRestaurantRepository>();
        restaurantRepository.Setup(item => item.LockOwnerRestaurantForVerificationAsync(
                12,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Domain.Entities.Restaurant?)null);
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = CreateUseCase(
            restaurantRepository,
            new Mock<IMediaRepository>(),
            unitOfWork,
            new Mock<ICloudinaryService>());

        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
        restaurantRepository.Verify(item => item.AddVerification(It.IsAny<RestaurantVerification>()), Times.Never);
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: Reject restaurant states with the exact OWN-06 conflict codes.
    /// <summary>Verifies the three documented conflict branches before media lookup.</summary>
    [Theory]
    [InlineData(RestaurantVerificationStatus.Pending, "VERIFICATION_ALREADY_PENDING")]
    [InlineData(RestaurantVerificationStatus.Verified, "RESTAURANT_ALREADY_VERIFIED")]
    [InlineData(RestaurantVerificationStatus.Rejected, "VERIFICATION_RESUBMIT_REQUIRED")]
    public async Task ExecuteAsync_WithConflictingRestaurantState_ShouldThrowConflict(
        RestaurantVerificationStatus status,
        string errorCode)
    {
        var restaurantRepository = new Mock<IRestaurantRepository>();
        restaurantRepository.Setup(item => item.LockOwnerRestaurantForVerificationAsync(
                12,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Restaurant
            {
                Id = 7,
                OwnerId = 12,
                VerificationStatus = status
            });
        var mediaRepository = new Mock<IMediaRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = CreateUseCase(
            restaurantRepository,
            mediaRepository,
            unitOfWork,
            new Mock<ICloudinaryService>());

        var act = () => useCase.ExecuteAsync(ValidRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be(errorCode);
        mediaRepository.Verify(item => item.GetOwnedByIdsAsync(
            It.IsAny<int>(),
            It.IsAny<IReadOnlyCollection<long>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-06: Reject media that is missing, belongs to another user, or was uploaded for another purpose.
    /// <summary>Verifies media ownership and verification_doc purpose at indexed request paths.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("image/upload/restaurant/photo.jpg")]
    [InlineData("raw/authenticated/report_evidence/evidence.pdf")]
    public async Task ExecuteAsync_WithInvalidVerificationMedia_ShouldThrowValidation(string? storageKey)
    {
        var restaurant = new Domain.Entities.Restaurant
        {
            Id = 7,
            OwnerId = 12,
            VerificationStatus = RestaurantVerificationStatus.Unverified
        };
        var restaurantRepository = new Mock<IRestaurantRepository>();
        restaurantRepository.Setup(item => item.LockOwnerRestaurantForVerificationAsync(
                12,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(item => item.GetOwnedByIdsAsync(
                12,
                It.IsAny<IReadOnlyCollection<long>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(storageKey is null
                ? Array.Empty<MediaFile>()
                : [VerificationMedia(9001, storageKey)]);
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = CreateUseCase(
            restaurantRepository,
            mediaRepository,
            unitOfWork,
            new Mock<ICloudinaryService>());

        var act = () => useCase.ExecuteAsync(ValidRequest(singleDocument: true), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be("Documents[0].MediaId");
        restaurant.VerificationStatus.Should().Be(RestaurantVerificationStatus.Unverified);
        restaurantRepository.Verify(item => item.AddVerification(It.IsAny<RestaurantVerification>()), Times.Never);
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-07: Do not commit when persistence fails after the aggregate is staged.
    /// <summary>Verifies transaction rollback-by-disposal for a failed OWN-06 write.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenSaveFails_ShouldNotCommit()
    {
        var restaurantRepository = new Mock<IRestaurantRepository>();
        restaurantRepository.Setup(item => item.LockOwnerRestaurantForVerificationAsync(
                12,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Restaurant
            {
                Id = 7,
                OwnerId = 12,
                VerificationStatus = RestaurantVerificationStatus.Unverified
            });
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(item => item.GetOwnedByIdsAsync(
                12,
                It.IsAny<IReadOnlyCollection<long>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([VerificationMedia(9001, "raw/authenticated/verification_doc/license.pdf")]);
        var unitOfWork = new Mock<IUnitOfWork>();
        var transaction = new Mock<IUnitOfWorkTransaction>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        unitOfWork.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database failure"));
        var useCase = CreateUseCase(
            restaurantRepository,
            mediaRepository,
            unitOfWork,
            new Mock<ICloudinaryService>(),
            transaction: transaction);

        var act = () => useCase.ExecuteAsync(ValidRequest(singleDocument: true), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Creates the use case with a real validator and an authenticated owner by default.</summary>
    private static SubmitVerificationUseCase CreateUseCase(
        Mock<IRestaurantRepository> restaurantRepository,
        Mock<IMediaRepository> mediaRepository,
        Mock<IUnitOfWork> unitOfWork,
        Mock<ICloudinaryService> cloudinaryService,
        int? ownerId = 12,
        Mock<IUnitOfWorkTransaction>? transaction = null)
    {
        transaction ??= new Mock<IUnitOfWorkTransaction>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(ownerId);
        return new SubmitVerificationUseCase(
            new SubmitVerificationRequestDtoValidator(),
            restaurantRepository.Object,
            mediaRepository.Object,
            currentUser.Object,
            cloudinaryService.Object,
            unitOfWork.Object);
    }

    /// <summary>Creates a complete valid OWN-06 request with normalized and optional fields.</summary>
    private static SubmitVerificationRequestDto ValidRequest(bool singleDocument = false) => new()
    {
        LegalName = "  Hộ kinh doanh Bún Bò Bà Diệu  ",
        BusinessLicenseNo = "  32A8012345  ",
        TaxCode = "  0401234567  ",
        OwnerNote = "  Bổ sung giấy ATTP  ",
        Documents = singleDocument
            ?
            [
                new VerificationDocumentRequestDto
                {
                    DocType = "business_license",
                    MediaId = 9001
                }
            ]
            :
            [
                new VerificationDocumentRequestDto
                {
                    DocType = "business_license",
                    MediaId = 9001
                },
                new VerificationDocumentRequestDto
                {
                    DocType = "storefront_photo",
                    MediaId = 9002
                }
            ]
    };

    /// <summary>Creates a caller-owned media row with a selected storage key.</summary>
    private static MediaFile VerificationMedia(long id, string storageKey) => new()
    {
        Id = id,
        UploaderId = 12,
        StorageKey = storageKey,
        Url = "private"
    };
}
