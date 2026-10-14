using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Restaurant.GetOwnerMenu;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.GetOwnerMenu;

public sealed class GetOwnerMenuUseCaseTests
{
    // TEST-01: Return owner dishes in menu order with the open submission summary.
    /// <summary>Verifies the complete OWN-09 response mapping.</summary>
    [Fact]
    public async Task ExecuteAsync_WithRestaurant_ShouldMapMenuAndOpenSubmission()
    {
        var restaurant = new Domain.Entities.Restaurant { Id = 7, Name = "Quán", Latitude = 1, Longitude = 2 };
        var dish = new Dish { Id = 105, Name = "Món", Restaurant = restaurant, SortOrder = 2 };
        restaurant.Dishes.Add(dish);
        restaurant.MenuSubmissions.Add(new MenuSubmission
        {
            Id = 14,
            Status = MenuSubmissionStatus.Pending,
            CreatedAt = DateTime.UtcNow
        });
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetOwnerMenuAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);

        var result = await CreateUseCase(repository).ExecuteAsync(CancellationToken.None);

        result.Dishes.Should().ContainSingle().Which.Id.Should().Be(105);
        result.Dishes[0].SortOrder.Should().Be(2);
        result.OpenSubmission.Should().BeEquivalentTo(new { Id = 14L, Status = "pending" });
    }

    // TEST-02: Return RESTAURANT_NOT_FOUND when the owner has no restaurant.
    /// <summary>Verifies the documented OWN-09 not-found path.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutRestaurant_ShouldThrowNotFound()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetOwnerMenuAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Domain.Entities.Restaurant?)null);

        var act = () => CreateUseCase(repository).ExecuteAsync(CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
    }

    // TEST-03: Return an empty dish list and no workflow summary for an empty menu.
    [Fact]
    public async Task ExecuteAsync_WithEmptyMenuAndNoOpenSubmission_ShouldReturnEmptyResponse()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetOwnerMenuAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Restaurant { Id = 7 });

        var result = await CreateUseCase(repository).ExecuteAsync(CancellationToken.None);

        result.Dishes.Should().BeEmpty();
        result.OpenSubmission.Should().BeNull();
    }

    // TEST-04: Map both draft and pending as the currently open submission.
    [Theory]
    [InlineData(MenuSubmissionStatus.Draft, "draft")]
    [InlineData(MenuSubmissionStatus.Pending, "pending")]
    public async Task ExecuteAsync_WithOpenSubmission_ShouldReturnItsStatus(
        MenuSubmissionStatus status,
        string expected)
    {
        var restaurant = new Domain.Entities.Restaurant { Id = 7 };
        restaurant.MenuSubmissions.Add(new MenuSubmission { Id = 14, Status = status });
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetOwnerMenuAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);

        var result = await CreateUseCase(repository).ExecuteAsync(CancellationToken.None);

        result.OpenSubmission!.Status.Should().Be(expected);
    }

    // TEST-05: Order dishes by sort order then id while retaining hidden dishes.
    [Fact]
    public async Task ExecuteAsync_WithEqualSortOrder_ShouldThenOrderByDishIdAndIncludeHidden()
    {
        var restaurant = new Domain.Entities.Restaurant { Id = 7 };
        restaurant.Dishes.Add(new Dish { Id = 12, Restaurant = restaurant, SortOrder = 2 });
        restaurant.Dishes.Add(new Dish
        {
            Id = 11,
            Restaurant = restaurant,
            SortOrder = 2,
            Status = DishStatus.Hidden
        });
        restaurant.Dishes.Add(new Dish { Id = 10, Restaurant = restaurant, SortOrder = 1 });
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetOwnerMenuAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);

        var result = await CreateUseCase(repository).ExecuteAsync(CancellationToken.None);

        result.Dishes.Select(item => item.Id).Should().Equal(10, 11, 12);
        result.Dishes[1].Status.Should().Be("hidden");
    }

    /// <summary>Creates the use case for an authenticated restaurant owner.</summary>
    private static GetOwnerMenuUseCase CreateUseCase(Mock<IRestaurantMenuRepository> repository)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(12);
        return new GetOwnerMenuUseCase(repository.Object, currentUser.Object);
    }
}
