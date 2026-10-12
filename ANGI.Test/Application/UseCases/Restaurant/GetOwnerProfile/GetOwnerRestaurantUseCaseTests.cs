using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Restaurant.GetOwnerProfile;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.Test.Application.UseCases.Restaurant.GetOwnerProfile;

public sealed class GetOwnerRestaurantUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 3, 0, 0, DateTimeKind.Utc);

    // TEST-01: Return the complete owner restaurant with the latest workflow summaries.
    /// <summary>Verifies OWN-02 mapping for gallery, verification, and open menu data.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenRestaurantExists_ShouldReturnCompleteOwnerProfile()
    {
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRestaurant());
        var cloudinary = new Mock<ICloudinaryService>();
        cloudinary.Setup(item => item.GetUrl("private/license.pdf"))
            .Returns("https://signed.test/license.pdf");
        var useCase = CreateUseCase(repository, cloudinary);

        var result = await useCase.ExecuteAsync(CancellationToken.None);

        result.Id.Should().Be(7);
        result.Images.Should().Equal("https://cdn.test/gallery.jpg");
        result.LatestVerification.Should().NotBeNull();
        result.LatestVerification!.Id.Should().Be(21);
        result.LatestVerification.Documents.Should().ContainSingle()
            .Which.Url.Should().Be("https://signed.test/license.pdf");
        result.OpenMenuSubmission.Should().BeEquivalentTo(new { Id = 14L, Status = "draft" });
        result.IsOpenNow.Should().BeTrue();
    }

    // TEST-02: Return RESTAURANT_NOT_FOUND when the authenticated owner has not registered a restaurant.
    /// <summary>Verifies the documented OWN-02 not-found error.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenRestaurantDoesNotExist_ShouldThrowNotFound()
    {
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(item => item.GetOwnerProfileAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Domain.Entities.Restaurant?)null);
        var useCase = CreateUseCase(repository, new Mock<ICloudinaryService>());

        var act = () => useCase.ExecuteAsync(CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
    }

    /// <summary>Creates OWN-02 with a fixed owner and deterministic Vietnam time.</summary>
    private static GetOwnerRestaurantUseCase CreateUseCase(
        Mock<IRestaurantRepository> repository,
        Mock<ICloudinaryService> cloudinary)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(12);
        return new GetOwnerRestaurantUseCase(
            repository.Object,
            currentUser.Object,
            cloudinary.Object,
            new FixedTimeProvider(Now));
    }

    /// <summary>Creates a representative aggregate returned by the owner profile repository.</summary>
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
            Longitude = 108.220116m,
            CreatedAt = Now,
            UpdatedAt = Now
        };
        restaurant.Images.Add(new RestaurantImage
        {
            MediaId = 101,
            SortOrder = 0,
            Media = new MediaFile { Id = 101, Url = "https://cdn.test/gallery.jpg" }
        });
        restaurant.BusinessHours.Add(new RestaurantBusinessHour
        {
            DayOfWeek = 5,
            OpenTime = new TimeOnly(6, 0),
            CloseTime = new TimeOnly(13, 30)
        });
        var verification = new RestaurantVerification
        {
            Id = 21,
            RestaurantId = 7,
            Status = VerificationStatus.Pending,
            LegalName = "Hộ kinh doanh Bà Diệu",
            BusinessLicenseNo = "32A8012345",
            SubmittedAt = Now.AddDays(-1)
        };
        verification.Documents.Add(new RestaurantVerificationDocument
        {
            MediaId = 9001,
            DocType = VerificationDocumentType.BusinessLicense,
            Media = new MediaFile { Id = 9001, StorageKey = "private/license.pdf", Url = "private" }
        });
        restaurant.Verifications.Add(verification);
        restaurant.MenuSubmissions.Add(new MenuSubmission
        {
            Id = 14,
            Status = MenuSubmissionStatus.Draft,
            CreatedAt = Now.AddHours(-1)
        });
        return restaurant;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        /// <summary>Initializes a clock fixed at the supplied UTC instant.</summary>
        public FixedTimeProvider(DateTime now) => _now = new DateTimeOffset(now);

        /// <summary>Returns the fixed instant for deterministic open-now calculations.</summary>
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
