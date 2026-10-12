using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.GetRestaurantDetail;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.Test.Application.UseCases.Restaurant.GetRestaurantDetail;

public sealed class GetRestaurantDetailUseCaseTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
    private readonly Mock<IRestaurantRepository> _repositoryMock = new();
    private readonly Mock<ICurrentUserService> _currentUserMock = new();
    private readonly Mock<TimeProvider> _timeProviderMock = new();
    private readonly GetRestaurantDetailRequestDtoValidator _validator = new();

    public GetRestaurantDetailUseCaseTests()
    {
        _timeProviderMock.Setup(t => t.GetUtcNow()).Returns(FixedNow);
    }

    private GetRestaurantDetailUseCase CreateUseCase() =>
        new(_repositoryMock.Object, _validator, _currentUserMock.Object, _timeProviderMock.Object);

    // TEST-01: Happy Path - Restaurant exists, return detail DTO
    [Fact]
    public async Task ExecuteAsync_WhenRestaurantExists_ShouldReturnRestaurantDetail()
    {
        var restaurantId = 7L;
        var request = new GetRestaurantDetailRequestDto { RestaurantId = restaurantId };

        var restaurant = new Domain.Entities.Restaurant
        {
            Id = restaurantId,
            Name = "Bún Bò Bà Diệu",
            Slug = "bun-bo-ba-dieu",
            AddressLine = "12 Lê Duẩn",
            District = "Quận Hải Châu",
            ProvinceName = "Thành phố Đà Nẵng",
            Latitude = 16.067812m,
            Longitude = 108.220116m,
            PriceLevel = 1,
            RatingAvg = 4.6m,
            RatingCount = 213,
            OperatingStatus = RestaurantOperatingStatus.Open,
            VerificationStatus = RestaurantVerificationStatus.Verified,
            ModerationStatus = RestaurantModerationStatus.Visible,
            BusinessHours = new List<RestaurantBusinessHour>
            {
                new()
                {
                    DayOfWeek = 1,
                    OpenTime = new TimeOnly(6, 0),
                    CloseTime = new TimeOnly(13, 30)
                }
            }
        };

        _repositoryMock
            .Setup(r => r.GetPublicDetailAsync(restaurantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);

        _currentUserMock.Setup(u => u.UserId).Returns((int?)null);

        var useCase = CreateUseCase();
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        result.Should().NotBeNull();
        result.Id.Should().Be(restaurantId);
        result.Name.Should().Be("Bún Bò Bà Diệu");
        result.RatingAvg.Should().Be(4.6m);
        result.OperatingStatus.Should().Be("open");
        result.BusinessHours.Should().HaveCount(1);
        result.BusinessHours[0].OpenTime.Should().Be("06:00");
        result.BusinessHours[0].CloseTime.Should().Be("13:30");
        result.MyReview.Should().BeNull();
        result.Menu.Should().BeNull();
    }

    // TEST-02: Exception Path - Restaurant does not exist, throw 404 RESTAURANT_NOT_FOUND
    [Fact]
    public async Task ExecuteAsync_WhenRestaurantNotFound_ShouldThrowNotFoundException()
    {
        var restaurantId = 999L;
        var request = new GetRestaurantDetailRequestDto { RestaurantId = restaurantId };

        _repositoryMock
            .Setup(r => r.GetPublicDetailAsync(restaurantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Domain.Entities.Restaurant?)null);

        var useCase = CreateUseCase();
        var act = async () => await useCase.ExecuteAsync(request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
    }

    // TEST-03: Authenticated Traveler has reviewed restaurant, include myReview
    [Fact]
    public async Task ExecuteAsync_WhenTravelerHasReviewed_ShouldIncludeMyReview()
    {
        var restaurantId = 7L;
        var travelerId = 42;
        var request = new GetRestaurantDetailRequestDto { RestaurantId = restaurantId };

        var restaurant = new Domain.Entities.Restaurant
        {
            Id = restaurantId,
            Name = "Bún Bò Bà Diệu",
            OperatingStatus = RestaurantOperatingStatus.Open,
            VerificationStatus = RestaurantVerificationStatus.Verified,
            ModerationStatus = RestaurantModerationStatus.Visible
        };

        var userReview = new RestaurantReview
        {
            Id = 640,
            RestaurantId = restaurantId,
            UserId = travelerId,
            Rating = 5,
            Content = "Nước dùng đậm đà",
            Status = ContentStatus.Visible,
            User = new User
            {
                Id = travelerId,
                DisplayName = "Traveler John"
            }
        };

        _repositoryMock
            .Setup(r => r.GetPublicDetailAsync(restaurantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);

        _currentUserMock.Setup(u => u.UserId).Returns(travelerId);

        _repositoryMock
            .Setup(r => r.GetTravelerReviewForRestaurantAsync(restaurantId, travelerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(userReview);

        var useCase = CreateUseCase();
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        result.Should().NotBeNull();
        result.MyReview.Should().NotBeNull();
        result.MyReview!.Id.Should().Be(640);
        result.MyReview.Rating.Should().Be(5);
        result.MyReview.Content.Should().Be("Nước dùng đậm đà");
    }
}
