using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.GetRestaurantMenu;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.GetRestaurantMenu;

public sealed class GetRestaurantMenuUseCaseTests
{
    private readonly Mock<IRestaurantRepository> _repositoryMock = new();
    private readonly GetRestaurantMenuRequestDtoValidator _validator = new();

    private GetRestaurantMenuUseCase CreateUseCase() =>
        new(_repositoryMock.Object, _validator);

    // TEST-01: Happy Path - Active menu dishes returned for existing restaurant
    [Fact]
    public async Task ExecuteAsync_WhenRestaurantExists_ShouldReturnRestaurantMenu()
    {
        var restaurantId = 7L;
        var request = new GetRestaurantMenuRequestDto
        {
            RestaurantId = restaurantId,
            IncludeUnavailable = true
        };

        var restaurant = new Domain.Entities.Restaurant
        {
            Id = restaurantId,
            Name = "Bún Bò Bà Diệu",
            ProvinceName = "Thành phố Đà Nẵng",
            Latitude = 16.067812m,
            Longitude = 108.220116m,
            RatingAvg = 4.6m
        };

        var dishes = new List<Dish>
        {
            new()
            {
                Id = 105,
                RestaurantId = restaurantId,
                Name = "Bún bò Huế",
                Price = 45000,
                DishForm = DishForm.Soup,
                IsAvailable = true,
                LikeCount = 128,
                DislikeCount = 9,
                SortOrder = 1,
                Restaurant = restaurant,
                DishTags = new List<DishTag>
                {
                    new()
                    {
                        Tag = new Tag
                        {
                            Id = 2,
                            TagType = TagType.MealTime,
                            Code = "lunch",
                            Name = "Bữa trưa"
                        }
                    }
                }
            }
        };

        _repositoryMock
            .Setup(r => r.ExistsAndActiveAsync(restaurantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _repositoryMock
            .Setup(r => r.GetActiveMenuDishesAsync(restaurantId, null, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dishes);

        var useCase = CreateUseCase();
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        result.Should().NotBeNull();
        result.RestaurantId.Should().Be(restaurantId);
        result.Dishes.Should().HaveCount(1);
        result.Dishes[0].Id.Should().Be(105);
        result.Dishes[0].Name.Should().Be("Bún bò Huế");
        result.Dishes[0].Price.Should().Be(45000);
        result.Dishes[0].DishForm.Should().Be("soup");
        result.Dishes[0].Tags.Should().HaveCount(1);
        result.Dishes[0].Tags[0].Code.Should().Be("lunch");
        result.Dishes[0].Restaurant.Id.Should().Be(restaurantId);
        result.Dishes[0].Rank.Should().BeNull();
    }

    // TEST-02: Exception Path - Restaurant does not exist, throw 404 RESTAURANT_NOT_FOUND
    [Fact]
    public async Task ExecuteAsync_WhenRestaurantNotFound_ShouldThrowNotFoundException()
    {
        var restaurantId = 999L;
        var request = new GetRestaurantMenuRequestDto { RestaurantId = restaurantId };

        _repositoryMock
            .Setup(r => r.ExistsAndActiveAsync(restaurantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var useCase = CreateUseCase();
        var act = async () => await useCase.ExecuteAsync(request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
    }
}
