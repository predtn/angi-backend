using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.ResubmitVerification;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.ResubmitVerification;

public sealed class ResubmitVerificationUseCaseTests
{
    private const int OwnerId = 12;
    private const long RestaurantId = 7;
    private const long PreviousId = 21;
    private static readonly DateTime SubmittedAt = new(2026, 10, 18, 3, 0, 0, DateTimeKind.Utc);

    // TEST-01: Create a pending submission linked to the previous one, attach owned documents and mark the
    // restaurant pending, for both statuses OWN-08 allows.
    /// <summary>Verifies the complete successful OWN-08 transaction and response mapping.</summary>
    [Theory]
    [InlineData(VerificationStatus.Rejected)]
    [InlineData(VerificationStatus.NeedsMoreInfo)]
    public async Task ExecuteAsync_WithResubmittableLatestVerification_ShouldCreatePendingVerification(
        VerificationStatus previousStatus)
    {
        var restaurant = Restaurant(RestaurantVerificationStatus.Rejected);
        var fixture = new Fixture(restaurant, previousStatus, latestId: PreviousId);
        RestaurantVerification? added = null;
        fixture.RestaurantRepository.Setup(item => item.AddVerification(It.IsAny<RestaurantVerification>()))
            .Callback<RestaurantVerification>(verification => added = verification);
        fixture.UnitOfWork.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                added!.Id = 22;
                added.SubmittedAt = SubmittedAt;
            })
            .ReturnsAsync(3);
        fixture.MediaRepository.Setup(item => item.GetOwnedByIdsAsync(
                OwnerId,
                It.IsAny<IReadOnlyCollection<long>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([VerificationMedia(9002, "raw/authenticated/verification_doc/attp.pdf")]);
        var cancellationToken = new CancellationTokenSource().Token;

        var result = await fixture.CreateUseCase().ExecuteAsync(PreviousId, ValidRequest(), cancellationToken);

        restaurant.VerificationStatus.Should().Be(RestaurantVerificationStatus.Pending);
        added.Should().NotBeNull();
        added!.RestaurantId.Should().Be(RestaurantId);
        added.SubmittedBy.Should().Be(OwnerId);
        added.PreviousId.Should().Be(PreviousId);
        added.Status.Should().Be(VerificationStatus.Pending);
        added.LegalName.Should().Be("Hộ kinh doanh Bún Bò Bà Diệu");
        added.BusinessLicenseNo.Should().Be("32A8012345");
        added.TaxCode.Should().Be("0401234567");
        added.OwnerNote.Should().Be("Đã bổ sung giấy ATTP theo yêu cầu");
        added.Documents.Should().ContainSingle()
            .Which.Should().Match<RestaurantVerificationDocument>(document =>
                document.DocType == VerificationDocumentType.FoodSafetyCert &&
                document.MediaId == 9002 &&
                document.Media == null);
        result.Id.Should().Be(22);
        result.RestaurantId.Should().Be(RestaurantId);
        result.Status.Should().Be("pending");
        result.PreviousId.Should().Be(PreviousId);
        result.SubmittedAt.Should().Be(SubmittedAt);
        result.Documents.Should().ContainSingle()
            .Which.Url.Should().Be("https://signed.test/attp.pdf");
        fixture.UnitOfWork.Verify(item => item.SaveChangesAsync(cancellationToken), Times.Once);
        fixture.Transaction.Verify(item => item.CommitAsync(cancellationToken), Times.Once);
    }

    // TEST-02: Run request validation before opening a transaction or querying persistence.
    /// <summary>Verifies first-statement validation.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidRequest_ShouldFailBeforePersistence()
    {
        var fixture = new Fixture(Restaurant(RestaurantVerificationStatus.Rejected), VerificationStatus.Rejected, PreviousId);
        var request = ValidRequest();
        request.Documents = [];

        var act = () => fixture.CreateUseCase().ExecuteAsync(PreviousId, request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        fixture.UnitOfWork.Verify(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        fixture.RestaurantRepository.Verify(item => item.LockOwnerRestaurantForVerificationAsync(
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Reject a missing authenticated user before opening the transaction.
    /// <summary>Verifies the standard UNAUTHORIZED guard for OWN-08.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutAuthenticatedUser_ShouldThrowUnauthorized()
    {
        var fixture = new Fixture(Restaurant(RestaurantVerificationStatus.Rejected), VerificationStatus.Rejected, PreviousId)
        {
            CallerId = null
        };

        var act = () => fixture.CreateUseCase().ExecuteAsync(PreviousId, ValidRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("UNAUTHORIZED");
        fixture.UnitOfWork.Verify(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Return RESTAURANT_NOT_FOUND when the authenticated owner has no restaurant.
    /// <summary>Verifies the ownership-scoped locked lookup and no-write behavior.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutRestaurant_ShouldThrowNotFound()
    {
        var fixture = new Fixture(restaurant: null, VerificationStatus.Rejected, PreviousId);

        var act = () => fixture.CreateUseCase().ExecuteAsync(PreviousId, ValidRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
        fixture.VerifyNothingWritten();
    }

    // TEST-05 (Exception Path 1): A verification id that does not exist, or belongs to another owner's restaurant,
    // looks the same to the caller: 404 VERIFICATION_NOT_FOUND, never a leak of the other record.
    /// <summary>Verifies the lookup is scoped to the caller's restaurant and writes nothing.</summary>
    // TEST-05: Verify ExecuteAsync with verification not of callers restaurant; should throw not found.
    [Fact]
    public async Task ExecuteAsync_WithVerificationNotOfCallersRestaurant_ShouldThrowNotFound()
    {
        var fixture = new Fixture(Restaurant(RestaurantVerificationStatus.Rejected), previousStatus: null, latestId: 99);

        var act = () => fixture.CreateUseCase().ExecuteAsync(999, ValidRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("VERIFICATION_NOT_FOUND");
        fixture.RestaurantRepository.Verify(item => item.GetVerificationStatusAsync(
            RestaurantId,
            999,
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.VerifyNothingWritten();
    }

    // TEST-06 (Exception Path 2): Only rejected / needs_more_info can be resubmitted.
    /// <summary>Verifies pending, approved and cancelled submissions raise 409 VERIFICATION_NOT_RESUBMITTABLE.</summary>
    // TEST-06: Verify ExecuteAsync with non resubmittable status; should throw conflict.
    [Theory]
    [InlineData(VerificationStatus.Pending)]
    [InlineData(VerificationStatus.Approved)]
    [InlineData(VerificationStatus.Cancelled)]
    public async Task ExecuteAsync_WithNonResubmittableStatus_ShouldThrowConflict(VerificationStatus previousStatus)
    {
        var fixture = new Fixture(Restaurant(RestaurantVerificationStatus.Rejected), previousStatus, PreviousId);

        var act = () => fixture.CreateUseCase().ExecuteAsync(PreviousId, ValidRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("VERIFICATION_NOT_RESUBMITTABLE");
        fixture.VerifyNothingWritten();
    }

    // TEST-07 (Exception Path 2): A newer submission exists, so the older rejected one can no longer be resubmitted.
    /// <summary>Verifies only the latest submission is resubmittable.</summary>
    // TEST-07: Verify ExecuteAsync with verification that is not the latest; should throw conflict.
    [Fact]
    public async Task ExecuteAsync_WithVerificationThatIsNotTheLatest_ShouldThrowConflict()
    {
        var fixture = new Fixture(Restaurant(RestaurantVerificationStatus.Pending), VerificationStatus.Rejected, latestId: 30);

        var act = () => fixture.CreateUseCase().ExecuteAsync(PreviousId, ValidRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("VERIFICATION_NOT_RESUBMITTABLE");
        fixture.VerifyNothingWritten();
    }

    // TEST-08 (Exception Path 2): The restaurant itself must not already be pending or verified.
    /// <summary>Verifies the restaurant-level guard even when the previous submission looks resubmittable.</summary>
    // TEST-08: Verify ExecuteAsync with pending or verified restaurant; should throw conflict.
    [Theory]
    [InlineData(RestaurantVerificationStatus.Pending)]
    [InlineData(RestaurantVerificationStatus.Verified)]
    public async Task ExecuteAsync_WithPendingOrVerifiedRestaurant_ShouldThrowConflict(
        RestaurantVerificationStatus restaurantStatus)
    {
        var fixture = new Fixture(Restaurant(restaurantStatus), VerificationStatus.Rejected, PreviousId);

        var act = () => fixture.CreateUseCase().ExecuteAsync(PreviousId, ValidRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("VERIFICATION_NOT_RESUBMITTABLE");
        fixture.VerifyNothingWritten();
    }

    // TEST-09: Reject media that is missing, belongs to another user, or was uploaded for another purpose.
    /// <summary>Verifies media ownership and verification_doc purpose at the indexed request path.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("image/upload/restaurant/photo.jpg")]
    [InlineData("raw/authenticated/report_evidence/evidence.pdf")]
    public async Task ExecuteAsync_WithInvalidVerificationMedia_ShouldThrowValidation(string? storageKey)
    {
        var restaurant = Restaurant(RestaurantVerificationStatus.Rejected);
        var fixture = new Fixture(restaurant, VerificationStatus.Rejected, PreviousId);
        fixture.MediaRepository.Setup(item => item.GetOwnedByIdsAsync(
                OwnerId,
                It.IsAny<IReadOnlyCollection<long>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(storageKey is null
                ? Array.Empty<MediaFile>()
                : [VerificationMedia(9002, storageKey)]);

        var act = () => fixture.CreateUseCase().ExecuteAsync(PreviousId, ValidRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be("Documents[0].MediaId");
        restaurant.VerificationStatus.Should().Be(RestaurantVerificationStatus.Rejected);
        fixture.VerifyNothingWritten();
    }

    // TEST-10: Do not commit when persistence fails after the aggregate is staged.
    /// <summary>Verifies transaction rollback-by-disposal for a failed OWN-08 write.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenSaveFails_ShouldNotCommit()
    {
        var fixture = new Fixture(Restaurant(RestaurantVerificationStatus.Rejected), VerificationStatus.Rejected, PreviousId);
        fixture.MediaRepository.Setup(item => item.GetOwnedByIdsAsync(
                OwnerId,
                It.IsAny<IReadOnlyCollection<long>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([VerificationMedia(9002, "raw/authenticated/verification_doc/attp.pdf")]);
        fixture.UnitOfWork.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database failure"));

        var act = () => fixture.CreateUseCase().ExecuteAsync(PreviousId, ValidRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        fixture.Transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Creates a complete valid OWN-08 request with normalized and optional fields.</summary>
    private static ResubmitVerificationRequestDto ValidRequest() => new()
    {
        LegalName = "  Hộ kinh doanh Bún Bò Bà Diệu  ",
        BusinessLicenseNo = "  32A8012345  ",
        TaxCode = "  0401234567  ",
        OwnerNote = "  Đã bổ sung giấy ATTP theo yêu cầu  ",
        Documents =
        [
            new VerificationDocumentRequestDto
            {
                DocType = "food_safety_cert",
                MediaId = 9002
            }
        ]
    };

    /// <summary>Creates the owner's restaurant in a given verification state.</summary>
    private static Domain.Entities.Restaurant Restaurant(RestaurantVerificationStatus status) => new()
    {
        Id = RestaurantId,
        OwnerId = OwnerId,
        VerificationStatus = status
    };

    /// <summary>Creates a caller-owned media row with a selected storage key.</summary>
    private static MediaFile VerificationMedia(long id, string storageKey) => new()
    {
        Id = id,
        UploaderId = OwnerId,
        StorageKey = storageKey,
        Url = "private"
    };

    /// <summary>Wires the mocks of one scenario; the repository answers for the owner's locked restaurant.</summary>
    private sealed class Fixture
    {
        public Fixture(Domain.Entities.Restaurant? restaurant, VerificationStatus? previousStatus, long? latestId)
        {
            RestaurantRepository.Setup(item => item.LockOwnerRestaurantForVerificationAsync(
                    OwnerId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(restaurant);
            RestaurantRepository.Setup(item => item.GetVerificationStatusAsync(
                    RestaurantId,
                    It.IsAny<long>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(previousStatus);
            RestaurantRepository.Setup(item => item.GetLatestVerificationIdAsync(
                    RestaurantId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(latestId);
            UnitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Transaction.Object);
            Cloudinary.Setup(item => item.GetUrl(It.IsAny<string>()))
                .Returns<string>(storageKey => $"https://signed.test/{Path.GetFileName(storageKey)}");
        }

        public Mock<IRestaurantRepository> RestaurantRepository { get; } = new();
        public Mock<IMediaRepository> MediaRepository { get; } = new();
        public Mock<IUnitOfWork> UnitOfWork { get; } = new();
        public Mock<IUnitOfWorkTransaction> Transaction { get; } = new();
        public Mock<ICloudinaryService> Cloudinary { get; } = new();
        public int? CallerId { get; init; } = ResubmitVerificationUseCaseTests.OwnerId;

        /// <summary>Creates the use case with the real validator.</summary>
        public ResubmitVerificationUseCase CreateUseCase()
        {
            var currentUser = new Mock<ICurrentUserService>();
            currentUser.SetupGet(item => item.UserId).Returns(CallerId);
            return new ResubmitVerificationUseCase(
                new ResubmitVerificationRequestDtoValidator(),
                RestaurantRepository.Object,
                MediaRepository.Object,
                currentUser.Object,
                Cloudinary.Object,
                UnitOfWork.Object);
        }

        /// <summary>Asserts no submission was staged and nothing was saved or committed.</summary>
        public void VerifyNothingWritten()
        {
            RestaurantRepository.Verify(item => item.AddVerification(It.IsAny<RestaurantVerification>()), Times.Never);
            UnitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
            Transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
