using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.ListVerifications;
using ANGI.Application.UseCases.Validators.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.ListVerifications;

public sealed class ListVerificationsUseCaseTests
{
    // TEST-01: Return a mapped page, signed document URL, trimmed province, and exact pagination metadata.
    /// <summary>Verifies the successful MOD-V1 authorization, normalization, query, and mapping path.</summary>
    [Fact]
    public async Task ExecuteAsync_WithAuthorizedRequest_ShouldReturnMappedPage()
    {
        var repository = AuthorizedRepository();
        repository.Setup(item => item.ListAsync(It.IsAny<ListVerificationsRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(([Verification()], 21));
        var cloudinary = new Mock<ICloudinaryService>();
        cloudinary.Setup(item => item.GetUrl("verification/license.pdf"))
            .Returns("https://signed.test/license.pdf");
        var request = new ListVerificationsRequestDto
        {
            Status = "PENDING",
            Province = "  Đà Nẵng  ",
            Page = 2,
            PageSize = 10
        };
        var useCase = CreateUseCase(repository, cloudinary.Object);

        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        result.Page.Should().Be(2);
        result.PageSize.Should().Be(10);
        result.TotalItems.Should().Be(21);
        result.TotalPages.Should().Be(3);
        result.Items.Should().ContainSingle();
        result.Items[0].Status.Should().Be("pending");
        result.Items[0].Documents[0].Url.Should().Be("https://signed.test/license.pdf");
        result.Items[0].Restaurant.Name.Should().Be("Bún Bò Bà Diệu");
        request.Status.Should().Be("pending");
        request.Province.Should().Be("Đà Nẵng");
    }

    // TEST-02: Validate before authorization or repository access.
    /// <summary>Verifies MOD-V1 first-statement validation for invalid pagination.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidRequest_ShouldFailBeforeAuthorization()
    {
        var repository = new Mock<IVerificationReviewRepository>();
        var useCase = CreateUseCase(repository, Mock.Of<ICloudinaryService>());

        var act = () => useCase.ExecuteAsync(
            new ListVerificationsRequestDto { Page = 0 },
            CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        repository.Verify(item => item.HasEffectivePermissionAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Reject an unauthenticated caller.
    /// <summary>Verifies the standard UNAUTHORIZED branch for MOD-V1.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutUser_ShouldThrowUnauthorized()
    {
        var useCase = CreateUseCase(
            new Mock<IVerificationReviewRepository>(),
            Mock.Of<ICloudinaryService>(),
            userId: null);

        var act = () => useCase.ExecuteAsync(new ListVerificationsRequestDto(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    // TEST-04: Reject a Mod whose effective RESTAURANT_VERIFY permission is denied.
    /// <summary>Verifies database-backed permission denial before reading the queue.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutEffectivePermission_ShouldThrowForbidden()
    {
        var repository = new Mock<IVerificationReviewRepository>();
        repository.Setup(item => item.HasEffectivePermissionAsync(9, "RESTAURANT_VERIFY", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var useCase = CreateUseCase(repository, Mock.Of<ICloudinaryService>());

        var act = () => useCase.ExecuteAsync(new ListVerificationsRequestDto(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ForbiddenException>();
        exception.Which.ErrorCode.Should().Be("FORBIDDEN");
        repository.Verify(item => item.ListAsync(
            It.IsAny<ListVerificationsRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: Reject non-Mod roles even when the permission boundary would grant access.
    /// <summary>Verifies MOD-V1 enforces role restrictions before checking permissions or reading submissions.</summary>
    [Theory]
    [InlineData("ADMIN")]
    [InlineData("TRAVELER")]
    [InlineData("RESTAURANT_OWNER")]
    public async Task ExecuteAsync_WithNonModRole_ShouldThrowForbidden(string role)
    {
        var repository = AuthorizedRepository();
        var useCase = CreateUseCase(repository, Mock.Of<ICloudinaryService>(), role: role);

        var act = () => useCase.ExecuteAsync(new ListVerificationsRequestDto(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ForbiddenException>();
        exception.Which.ErrorCode.Should().Be("FORBIDDEN");
        repository.Verify(item => item.HasEffectivePermissionAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(item => item.ListAsync(
            It.IsAny<ListVerificationsRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Creates MOD-V1 with real validation and a configurable authenticated identity.</summary>
    private static ListVerificationsUseCase CreateUseCase(
        Mock<IVerificationReviewRepository> repository,
        ICloudinaryService cloudinaryService,
        int? userId = 9,
        string role = "MOD")
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(userId);
        currentUser.SetupGet(item => item.Role).Returns(role);
        return new ListVerificationsUseCase(
            new ListVerificationsRequestDtoValidator(),
            repository.Object,
            currentUser.Object,
            cloudinaryService,
            TimeProvider.System);
    }

    /// <summary>Creates a repository mock that grants RESTAURANT_VERIFY to the default Mod.</summary>
    private static Mock<IVerificationReviewRepository> AuthorizedRepository()
    {
        var repository = new Mock<IVerificationReviewRepository>();
        repository.Setup(item => item.HasEffectivePermissionAsync(9, "RESTAURANT_VERIFY", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return repository;
    }

    /// <summary>Creates the smallest complete aggregate required by the MOD-V1 mapper.</summary>
    private static RestaurantVerification Verification()
    {
        var restaurant = new Domain.Entities.Restaurant
        {
            Id = 7,
            Name = "Bún Bò Bà Diệu",
            Slug = "bun-bo-ba-dieu",
            AddressLine = "12 Lê Duẩn",
            ProvinceName = "Đà Nẵng",
            Latitude = 16.06m,
            Longitude = 108.22m,
            OperatingStatus = RestaurantOperatingStatus.TemporarilyClosed
        };
        return new RestaurantVerification
        {
            Id = 21,
            RestaurantId = restaurant.Id,
            Restaurant = restaurant,
            Status = VerificationStatus.Pending,
            LegalName = "Hộ kinh doanh Bún Bò Bà Diệu",
            BusinessLicenseNo = "32A8012345",
            SubmittedAt = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc),
            Documents =
            [
                new RestaurantVerificationDocument
                {
                    MediaId = 9001,
                    DocType = VerificationDocumentType.BusinessLicense,
                    Media = new MediaFile { Id = 9001, StorageKey = "verification/license.pdf" }
                }
            ]
        };
    }
}
