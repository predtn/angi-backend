using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Moderation.GetVerificationReview;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.GetVerificationReview;

public sealed class GetVerificationReviewUseCaseTests
{
    // TEST-01: Return flattened verification details, complete restaurant data, previous submission, and signed documents.
    /// <summary>Verifies the complete successful MOD-V2 contract mapping.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenVerificationExists_ShouldReturnCompleteDetail()
    {
        var repository = AuthorizedRepository();
        var verification = VerificationWithPrevious();
        repository.Setup(item => item.GetDetailAsync(21, It.IsAny<CancellationToken>()))
            .ReturnsAsync(verification);
        var cloudinary = new Mock<ICloudinaryService>();
        cloudinary.Setup(item => item.GetUrl(It.IsAny<string>()))
            .Returns<string>(key => $"https://signed.test/{key}");
        var useCase = CreateUseCase(repository, cloudinary.Object);

        var result = await useCase.ExecuteAsync(21, CancellationToken.None);

        result.Id.Should().Be(21);
        result.Status.Should().Be("pending");
        result.Documents[0].Url.Should().Be("https://signed.test/current.pdf");
        result.Restaurant.Id.Should().Be(7);
        result.Restaurant.LatestVerification!.Id.Should().Be(21);
        result.Previous.Should().NotBeNull();
        result.Previous!.Id.Should().Be(20);
        result.Previous.Documents[0].Url.Should().Be("https://signed.test/previous.pdf");
    }

    // TEST-02: Return the documented not-found error after authorization.
    /// <summary>Verifies MOD-V2 VERIFICATION_NOT_FOUND and no response mapping.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenVerificationMissing_ShouldThrowNotFound()
    {
        var repository = AuthorizedRepository();
        repository.Setup(item => item.GetDetailAsync(404, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RestaurantVerification?)null);
        var useCase = CreateUseCase(repository, Mock.Of<ICloudinaryService>());

        var act = () => useCase.ExecuteAsync(404, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("VERIFICATION_NOT_FOUND");
    }

    // TEST-03: Reject missing effective permission before revealing whether a verification exists.
    /// <summary>Verifies MOD-V2 resource existence is not disclosed to an unauthorized Mod.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutEffectivePermission_ShouldThrowForbiddenBeforeLookup()
    {
        var repository = new Mock<IVerificationReviewRepository>();
        repository.Setup(item => item.HasEffectivePermissionAsync(9, "RESTAURANT_VERIFY", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var useCase = CreateUseCase(repository, Mock.Of<ICloudinaryService>());

        var act = () => useCase.ExecuteAsync(21, CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenException>();
        repository.Verify(item => item.GetDetailAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Reject non-Mod roles before revealing verification details.
    /// <summary>Verifies MOD-V2 rejects Admin even when RESTAURANT_VERIFY would be granted.</summary>
    [Theory]
    [InlineData("ADMIN")]
    [InlineData("TRAVELER")]
    [InlineData("RESTAURANT_OWNER")]
    public async Task ExecuteAsync_WithNonModRole_ShouldThrowForbidden(string role)
    {
        var repository = AuthorizedRepository();
        var useCase = CreateUseCase(repository, Mock.Of<ICloudinaryService>(), role);

        var act = () => useCase.ExecuteAsync(21, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ForbiddenException>();
        exception.Which.ErrorCode.Should().Be("FORBIDDEN");
        repository.Verify(item => item.GetDetailAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Creates MOD-V2 with a configurable authenticated role.</summary>
    private static GetVerificationReviewUseCase CreateUseCase(
        Mock<IVerificationReviewRepository> repository,
        ICloudinaryService cloudinaryService,
        string role = "MOD")
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(9);
        currentUser.SetupGet(item => item.Role).Returns(role);
        return new GetVerificationReviewUseCase(
            repository.Object,
            currentUser.Object,
            cloudinaryService,
            TimeProvider.System);
    }

    /// <summary>Creates a repository mock that grants RESTAURANT_VERIFY.</summary>
    private static Mock<IVerificationReviewRepository> AuthorizedRepository()
    {
        var repository = new Mock<IVerificationReviewRepository>();
        repository.Setup(item => item.HasEffectivePermissionAsync(9, "RESTAURANT_VERIFY", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return repository;
    }

    /// <summary>Creates a current and previous verification inside a complete owner restaurant graph.</summary>
    private static RestaurantVerification VerificationWithPrevious()
    {
        var previous = new RestaurantVerification
        {
            Id = 20,
            RestaurantId = 7,
            Status = VerificationStatus.NeedsMoreInfo,
            LegalName = "Previous Legal Name",
            BusinessLicenseNo = "OLD-001",
            SubmittedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            Documents =
            [
                new RestaurantVerificationDocument
                {
                    MediaId = 9000,
                    DocType = VerificationDocumentType.BusinessLicense,
                    Media = new MediaFile { Id = 9000, StorageKey = "previous.pdf" }
                }
            ]
        };
        var restaurant = new Domain.Entities.Restaurant
        {
            Id = 7,
            OwnerId = 12,
            Name = "Bún Bò Bà Diệu",
            Slug = "bun-bo-ba-dieu",
            AddressLine = "12 Lê Duẩn",
            Latitude = 16.06m,
            Longitude = 108.22m,
            OperatingStatus = RestaurantOperatingStatus.TemporarilyClosed,
            CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var current = new RestaurantVerification
        {
            Id = 21,
            RestaurantId = restaurant.Id,
            Restaurant = restaurant,
            PreviousId = previous.Id,
            Previous = previous,
            Status = VerificationStatus.Pending,
            LegalName = "Current Legal Name",
            BusinessLicenseNo = "NEW-001",
            SubmittedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            Documents =
            [
                new RestaurantVerificationDocument
                {
                    MediaId = 9001,
                    DocType = VerificationDocumentType.BusinessLicense,
                    Media = new MediaFile { Id = 9001, StorageKey = "current.pdf" }
                }
            ]
        };
        restaurant.Verifications = [previous, current];
        previous.Restaurant = restaurant;
        return current;
    }
}
