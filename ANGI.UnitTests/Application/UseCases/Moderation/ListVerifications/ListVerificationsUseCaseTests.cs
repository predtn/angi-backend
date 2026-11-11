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

/// <summary>Verifies ListVerificationsUseCase validates input, enforces the MOD permission and maps the verification page.</summary>
public sealed class ListVerificationsUseCaseTests
{
    // TEST-01: Return a mapped page, signed document URL, trimmed province, and exact pagination metadata.
    /// <summary>Verifies the successful MOD-V1 authorization, normalization, query, and mapping path.</summary>
    [Fact]
    public async Task ExecuteAsync_WithAuthorizedRequest_ShouldReturnMappedPage()
    {
        // STEP-01: Get repository from AuthorizedRepository.
        var repository = AuthorizedRepository();
        // STEP-02: Mock ICloudinaryService; stub repository.ListAsync and cloudinary.GetUrl.
        repository.Setup(item => item.ListAsync(It.IsAny<ListVerificationsRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(([Verification()], 21));
        var cloudinary = new Mock<ICloudinaryService>();
        cloudinary.Setup(item => item.GetUrl("verification/license.pdf"))
            .Returns("https://signed.test/license.pdf");
        // STEP-03: Create request as ListVerificationsRequestDto.
        var request = new ListVerificationsRequestDto
        {
            Status = "PENDING",
            Province = "  Đà Nẵng  ",
            Page = 2,
            PageSize = 10
        };
        // STEP-04: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, cloudinary.Object);

        // STEP-05: Run useCase.ExecuteAsync with request and keep result.
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // STEP-06: Assert result.Page is 2, result.PageSize is 10, result.TotalItems is 21, result.TotalPages is 3 and 4 more.
        result.Page.Should().Be(2);
        result.PageSize.Should().Be(10);
        result.TotalItems.Should().Be(21);
        result.TotalPages.Should().Be(3);
        result.Items.Should().ContainSingle();
        result.Items[0].Status.Should().Be("pending");
        result.Items[0].Documents[0].Url.Should().Be("https://signed.test/license.pdf");
        result.Items[0].Restaurant.Name.Should().Be("Bún Bò Bà Diệu");
        // STEP-07: Assert request.Status is "pending" and request.Province is the expected text.
        request.Status.Should().Be("pending");
        request.Province.Should().Be("Đà Nẵng");
    }

    // TEST-02: Validate before authorization or repository access.
    /// <summary>Verifies MOD-V1 first-statement validation for invalid pagination.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidRequest_ShouldFailBeforeAuthorization()
    {
        // STEP-01: Mock IVerificationReviewRepository.
        var repository = new Mock<IVerificationReviewRepository>();
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, Mock.Of<ICloudinaryService>());

        // STEP-03: Wrap useCase.ExecuteAsync with ListVerificationsRequestDto { Page = 0 } in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(
            new ListVerificationsRequestDto { Page = 0 },
            CancellationToken.None);

        // STEP-04: Assert act throws ValidationException.
        await act.Should().ThrowAsync<ValidationException>();
        // STEP-05: Verify HasEffectivePermissionAsync is never called.
        repository.Verify(item => item.HasEffectivePermissionAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Reject an unauthenticated caller.
    /// <summary>Verifies the standard UNAUTHORIZED branch for MOD-V1.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutUser_ShouldThrowUnauthorized()
    {
        // STEP-01: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            new Mock<IVerificationReviewRepository>(),
            Mock.Of<ICloudinaryService>(),
            userId: null);

        // STEP-02: Wrap useCase.ExecuteAsync with an empty ListVerificationsRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(new ListVerificationsRequestDto(), CancellationToken.None);

        // STEP-03: Assert act throws UnauthorizedException and exception.ErrorCode is "UNAUTHORIZED".
        var exception = await act.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    // TEST-04: Reject a Mod whose effective RESTAURANT_VERIFY permission is denied.
    /// <summary>Verifies database-backed permission denial before reading the queue.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutEffectivePermission_ShouldThrowForbidden()
    {
        // STEP-01: Mock IVerificationReviewRepository; stub repository.HasEffectivePermissionAsync.
        var repository = new Mock<IVerificationReviewRepository>();
        repository.Setup(item => item.HasEffectivePermissionAsync(9, "RESTAURANT_VERIFY", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, Mock.Of<ICloudinaryService>());

        // STEP-03: Wrap useCase.ExecuteAsync with an empty ListVerificationsRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(new ListVerificationsRequestDto(), CancellationToken.None);

        // STEP-04: Assert act throws ForbiddenException and exception.ErrorCode is "FORBIDDEN".
        var exception = await act.Should().ThrowAsync<ForbiddenException>();
        exception.Which.ErrorCode.Should().Be("FORBIDDEN");
        // STEP-05: Verify ListAsync is never called.
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
        // STEP-01: Get repository from AuthorizedRepository.
        var repository = AuthorizedRepository();
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, Mock.Of<ICloudinaryService>(), role: role);

        // STEP-03: Wrap useCase.ExecuteAsync with an empty ListVerificationsRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(new ListVerificationsRequestDto(), CancellationToken.None);

        // STEP-04: Assert act throws ForbiddenException and exception.ErrorCode is "FORBIDDEN".
        var exception = await act.Should().ThrowAsync<ForbiddenException>();
        exception.Which.ErrorCode.Should().Be("FORBIDDEN");
        // STEP-05: Verify HasEffectivePermissionAsync is never called and ListAsync is never called.
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
