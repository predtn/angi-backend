using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.Browse;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.Browse;

public sealed class BrowseRestaurantsUseCaseTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    // TEST-01: Browse restaurants with valid request returns paged cards
    /// <summary>Verifies that the use case coordinates validator and repository successfully.</summary>
    [Fact]
    public async Task ExecuteAsync_WithValidRequest_ShouldReturnPagedRestaurants()
    {
        var request = new BrowseRestaurantsRequestDto
        {
            Q = "Bún Bò",
            Province = "Thành phố Đà Nẵng",
            Page = 1,
            PageSize = 20
        };

        var expectedCards = new[]
        {
            new RestaurantCardDto
            {
                Id = 1,
                Name = "Bún Bò Bà Diệu",
                Slug = "bun-bo-ba-dieu",
                AddressLine = "12 Lê Duẩn",
                ProvinceName = "Thành phố Đà Nẵng",
                RatingAvg = 4.8m,
                RatingCount = 100,
                OperatingStatus = "open",
                IsOpenNow = true
            }
        };

        var expectedPaged = new Paged<RestaurantCardDto>
        {
            Items = expectedCards,
            Page = 1,
            PageSize = 20,
            TotalItems = 1,
            TotalPages = 1
        };

        var repositoryMock = new Mock<IRestaurantRepository>();
        repositoryMock
            .Setup(r => r.BrowseAsync(request, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPaged);

        var currentUserMock = new Mock<ICurrentUserService>();
        currentUserMock.Setup(u => u.Role).Returns("TRAVELER");

        var timeProviderMock = new Mock<TimeProvider>();
        timeProviderMock.Setup(t => t.GetUtcNow()).Returns(FixedNow);

        var validator = new BrowseRestaurantsRequestDtoValidator();
        var useCase = new BrowseRestaurantsUseCase(
            repositoryMock.Object,
            validator,
            timeProviderMock.Object,
            currentUserMock.Object);

        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        result.Should().NotBeNull();
        result.TotalItems.Should().Be(1);
        result.Items.Should().HaveCount(1);
        result.Items[0].Name.Should().Be("Bún Bò Bà Diệu");
        repositoryMock.Verify(r => r.BrowseAsync(request, FixedNow, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Browse restaurants when caller is an unauthenticated Guest
    /// <summary>Verifies that the use case supports guest callers without throwing.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenCallerIsGuest_ShouldBrowseSuccessfully()
    {
        var request = new BrowseRestaurantsRequestDto
        {
            Sort = "rating",
            Page = 1,
            PageSize = 10
        };

        var expectedPaged = new Paged<RestaurantCardDto>
        {
            Items = Array.Empty<RestaurantCardDto>(),
            Page = 1,
            PageSize = 10,
            TotalItems = 0,
            TotalPages = 0
        };

        var repositoryMock = new Mock<IRestaurantRepository>();
        repositoryMock
            .Setup(r => r.BrowseAsync(request, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPaged);

        var currentUserMock = new Mock<ICurrentUserService>();
        currentUserMock.Setup(u => u.Role).Returns((string?)null);

        var timeProviderMock = new Mock<TimeProvider>();
        timeProviderMock.Setup(t => t.GetUtcNow()).Returns(FixedNow);

        var validator = new BrowseRestaurantsRequestDtoValidator();
        var useCase = new BrowseRestaurantsUseCase(
            repositoryMock.Object,
            validator,
            timeProviderMock.Object,
            currentUserMock.Object);

        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        result.Should().NotBeNull();
        result.TotalItems.Should().Be(0);
        result.Items.Should().BeEmpty();
    }

    // TEST-03: Throw ValidationException when request violates validation constraints
    /// <summary>Verifies that sorting by distance without coordinates throws ValidationException.</summary>
    [Fact]
    public async Task ExecuteAsync_WithSortDistanceMissingCoordinates_ShouldThrowValidationException()
    {
        var request = new BrowseRestaurantsRequestDto
        {
            Sort = "distance",
            Lat = null,
            Lng = null
        };

        var repositoryMock = new Mock<IRestaurantRepository>();
        var currentUserMock = new Mock<ICurrentUserService>();
        var timeProviderMock = new Mock<TimeProvider>();

        var validator = new BrowseRestaurantsRequestDtoValidator();
        var useCase = new BrowseRestaurantsUseCase(
            repositoryMock.Object,
            validator,
            timeProviderMock.Object,
            currentUserMock.Object);

        var act = async () => await useCase.ExecuteAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        repositoryMock.Verify(r => r.BrowseAsync(It.IsAny<BrowseRestaurantsRequestDto>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
