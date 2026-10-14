using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Restaurant.GetVerificationStatus;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.Test.Application.UseCases.Restaurant.GetVerificationStatus;

public sealed class GetVerificationStatusUseCaseTests
{
    private static readonly DateTime FirstSubmittedAt = new(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SecondSubmittedAt = new(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ReviewedAt = new(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc);

    // TEST-01: Happy Path - Return verification status and ordered verification history.
    /// <summary>Verifies that OWN-07 returns the restaurant verification status and history ordered newest first.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenRestaurantExistsWithVerifications_ShouldReturnStatusAndOrderedHistory()
    {
        const int ownerId = 12;
        var restaurant = CreateRestaurantWithVerifications(ownerId);

        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetOwnerRestaurantWithVerificationsAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);

        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService.Setup(item => item.UserId).Returns(ownerId);

        var cloudinaryService = new Mock<ICloudinaryService>();
        cloudinaryService.Setup(item => item.GetUrl(It.IsAny<string>()))
            .Returns<string>(key => $"https://signed.test/{key}");

        var useCase = new GetVerificationStatusUseCase(
            repository.Object,
            currentUserService.Object,
            cloudinaryService.Object);

        var result = await useCase.ExecuteAsync(CancellationToken.None);

        result.Should().NotBeNull();
        result.VerificationStatus.Should().Be("pending");
        result.Items.Should().HaveCount(2);

        // First item should be the newer submission (SecondSubmittedAt)
        var latestItem = result.Items[0];
        latestItem.Id.Should().Be(21);
        latestItem.Status.Should().Be("pending");
        latestItem.LegalName.Should().Be("Hộ kinh doanh Bún Bò Bà Diệu Mới");
        latestItem.SubmittedAt.Should().Be(SecondSubmittedAt);
        latestItem.Documents.Should().HaveCount(1);
        latestItem.Documents[0].DocType.Should().Be("business_license");
        latestItem.Documents[0].Url.Should().Be("https://signed.test/docs/license2.pdf");
        latestItem.ReviewedBy.Should().BeNull();

        // Second item should be the older submission (FirstSubmittedAt)
        var olderItem = result.Items[1];
        olderItem.Id.Should().Be(20);
        olderItem.Status.Should().Be("needs_more_info");
        olderItem.LegalName.Should().Be("Hộ kinh doanh Bún Bò Bà Diệu");
        olderItem.SubmittedAt.Should().Be(FirstSubmittedAt);
        olderItem.ReviewedBy.Should().NotBeNull();
        olderItem.ReviewedBy!.DisplayName.Should().Be("Mod Bảo");
        olderItem.ReviewedAt.Should().Be(ReviewedAt);
        olderItem.ReviewNote.Should().Be("Vui lòng bổ sung giấy chứng nhận ATTP.");
    }

    // TEST-02: Happy Path - Return unverified status and empty items list when no verifications exist.
    /// <summary>Verifies that an unverified restaurant returns an empty items array.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenRestaurantHasNoVerifications_ShouldReturnUnverifiedWithEmptyItems()
    {
        const int ownerId = 12;
        var restaurant = new Domain.Entities.Restaurant
        {
            Id = 7,
            OwnerId = ownerId,
            VerificationStatus = RestaurantVerificationStatus.Unverified,
            Verifications = new List<RestaurantVerification>()
        };

        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetOwnerRestaurantWithVerificationsAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);

        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService.Setup(item => item.UserId).Returns(ownerId);

        var useCase = new GetVerificationStatusUseCase(
            repository.Object,
            currentUserService.Object,
            Mock.Of<ICloudinaryService>());

        var result = await useCase.ExecuteAsync(CancellationToken.None);

        result.Should().NotBeNull();
        result.VerificationStatus.Should().Be("unverified");
        result.Items.Should().BeEmpty();
    }

    // TEST-03: Exception Path - Throws 401 UNAUTHORIZED when user is unauthenticated.
    /// <summary>Verifies that an unauthenticated caller cannot access OWN-07.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenUserNotAuthenticated_ShouldThrowUnauthorizedException()
    {
        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService.Setup(item => item.UserId).Returns((int?)null);

        var useCase = new GetVerificationStatusUseCase(
            Mock.Of<IRestaurantRepository>(),
            currentUserService.Object,
            Mock.Of<ICloudinaryService>());

        var act = () => useCase.ExecuteAsync(CancellationToken.None);

        var exception = await act.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    // TEST-04: Exception Path - Throws 404 RESTAURANT_NOT_FOUND when owner has no restaurant.
    /// <summary>Verifies that an owner without a restaurant receives RESTAURANT_NOT_FOUND.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenRestaurantNotFound_ShouldThrowNotFoundException()
    {
        const int ownerId = 12;
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetOwnerRestaurantWithVerificationsAsync(ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Domain.Entities.Restaurant?)null);

        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService.Setup(item => item.UserId).Returns(ownerId);

        var useCase = new GetVerificationStatusUseCase(
            repository.Object,
            currentUserService.Object,
            Mock.Of<ICloudinaryService>());

        var act = () => useCase.ExecuteAsync(CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
    }

    private static Domain.Entities.Restaurant CreateRestaurantWithVerifications(int ownerId)
    {
        var reviewer = new User
        {
            Id = 5,
            DisplayName = "Mod Bảo",
            AvatarMedia = new MediaFile { Url = "https://cdn.test/avatar.jpg" }
        };

        var firstVerification = new RestaurantVerification
        {
            Id = 20,
            RestaurantId = 7,
            SubmittedBy = ownerId,
            LegalName = "Hộ kinh doanh Bún Bò Bà Diệu",
            BusinessLicenseNo = "32A8012345",
            Status = VerificationStatus.NeedsMoreInfo,
            SubmittedAt = FirstSubmittedAt,
            ReviewedBy = reviewer.Id,
            Reviewer = reviewer,
            ReviewedAt = ReviewedAt,
            ReviewNote = "Vui lòng bổ sung giấy chứng nhận ATTP.",
            Documents = new List<RestaurantVerificationDocument>
            {
                new()
                {
                    Id = 101,
                    VerificationId = 20,
                    DocType = VerificationDocumentType.BusinessLicense,
                    MediaId = 9001,
                    Media = new MediaFile { Id = 9001, StorageKey = "docs/license1.pdf" }
                }
            }
        };

        var secondVerification = new RestaurantVerification
        {
            Id = 21,
            RestaurantId = 7,
            SubmittedBy = ownerId,
            PreviousId = 20,
            LegalName = "Hộ kinh doanh Bún Bò Bà Diệu Mới",
            BusinessLicenseNo = "32A8012345",
            Status = VerificationStatus.Pending,
            SubmittedAt = SecondSubmittedAt,
            Documents = new List<RestaurantVerificationDocument>
            {
                new()
                {
                    Id = 102,
                    VerificationId = 21,
                    DocType = VerificationDocumentType.BusinessLicense,
                    MediaId = 9002,
                    Media = new MediaFile { Id = 9002, StorageKey = "docs/license2.pdf" }
                }
            }
        };

        return new Domain.Entities.Restaurant
        {
            Id = 7,
            OwnerId = ownerId,
            VerificationStatus = RestaurantVerificationStatus.Pending,
            Verifications = new List<RestaurantVerification> { firstVerification, secondVerification }
        };
    }
}
