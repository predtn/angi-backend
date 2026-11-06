using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Restaurant.GetOwnerMenu;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.GetOwnerMenu;

/// <summary>Verifies GetOwnerMenuUseCase returns the owner's full menu in order with the open submission summary.</summary>
public sealed class GetOwnerMenuUseCaseTests
{
    // TEST-01: Return owner dishes in menu order with the open submission summary.
    /// <summary>Verifies the complete OWN-09 response mapping.</summary>
    [Fact]
    public async Task ExecuteAsync_WithRestaurant_ShouldMapMenuAndOpenSubmission()
    {
        // STEP-01: Create restaurant as Domain.Entities.Restaurant, create dish as Dish, add dish to restaurant.Dishes and 1 more.
        var restaurant = new Domain.Entities.Restaurant { Id = 7, Name = "Quán", Latitude = 1, Longitude = 2 };
        var dish = new Dish { Id = 105, Name = "Món", Restaurant = restaurant, SortOrder = 2 };
        restaurant.Dishes.Add(dish);
        restaurant.MenuSubmissions.Add(new MenuSubmission
        {
            Id = 14,
            Status = MenuSubmissionStatus.Pending,
            CreatedAt = DateTime.UtcNow
        });
        // STEP-02: Mock IRestaurantMenuRepository; stub repository.GetOwnerMenuAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetOwnerMenuAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);

        // STEP-03: Run CreateUseCase().ExecuteAsync and keep result.
        var result = await CreateUseCase(repository).ExecuteAsync(CancellationToken.None);

        // STEP-04: Assert result.Dishes has exactly one matching item, result.Dishes.Id is 105, result.Dishes[0].SortOrder is 2 and 1 more.
        result.Dishes.Should().ContainSingle().Which.Id.Should().Be(105);
        result.Dishes[0].SortOrder.Should().Be(2);
        result.OpenSubmission.Should().BeEquivalentTo(new { Id = 14L, Status = "pending" });
    }

    // TEST-02: Return RESTAURANT_NOT_FOUND when the owner has no restaurant.
    /// <summary>Verifies the documented OWN-09 not-found path.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutRestaurant_ShouldThrowNotFound()
    {
        // STEP-01: Mock IRestaurantMenuRepository; stub repository.GetOwnerMenuAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetOwnerMenuAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Domain.Entities.Restaurant?)null);

        // STEP-02: Wrap CreateUseCase().ExecuteAsync in act so its outcome can be asserted.
        var act = () => CreateUseCase(repository).ExecuteAsync(CancellationToken.None);

        // STEP-03: Assert act throws NotFoundException and exception.ErrorCode is "RESTAURANT_NOT_FOUND".
        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
    }

    // TEST-03: Return an empty dish list and no workflow summary for an empty menu.
    /// <summary>Ensures an empty menu returns an empty list instead of an error.</summary>
    [Fact]
    public async Task ExecuteAsync_WithEmptyMenuAndNoOpenSubmission_ShouldReturnEmptyResponse()
    {
        // STEP-01: Mock IRestaurantMenuRepository; stub repository.GetOwnerMenuAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetOwnerMenuAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Restaurant { Id = 7 });

        // STEP-02: Run CreateUseCase().ExecuteAsync and keep result.
        var result = await CreateUseCase(repository).ExecuteAsync(CancellationToken.None);

        // STEP-03: Assert result.Dishes is empty and result.OpenSubmission is null.
        result.Dishes.Should().BeEmpty();
        result.OpenSubmission.Should().BeNull();
    }

    // TEST-04: Map both draft and pending as the currently open submission.
    /// <summary>Ensures both draft and pending submissions are reported as open.</summary>
    [Theory]
    [InlineData(MenuSubmissionStatus.Draft, "draft")]
    [InlineData(MenuSubmissionStatus.Pending, "pending")]
    public async Task ExecuteAsync_WithOpenSubmission_ShouldReturnItsStatus(
        MenuSubmissionStatus status,
        string expected)
    {
        // STEP-01: Create restaurant as Domain.Entities.Restaurant and add a MenuSubmission to restaurant.MenuSubmissions.
        var restaurant = new Domain.Entities.Restaurant { Id = 7 };
        restaurant.MenuSubmissions.Add(new MenuSubmission { Id = 14, Status = status });
        // STEP-02: Mock IRestaurantMenuRepository; stub repository.GetOwnerMenuAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetOwnerMenuAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);

        // STEP-03: Run CreateUseCase().ExecuteAsync and keep result.
        var result = await CreateUseCase(repository).ExecuteAsync(CancellationToken.None);

        // STEP-04: Assert result.OpenSubmission.Status is expected.
        result.OpenSubmission!.Status.Should().Be(expected);
    }

    // TEST-05: Order dishes by sort order then id while retaining hidden dishes.
    /// <summary>Ensures a stable order and that owners still see their hidden dishes.</summary>
    [Fact]
    public async Task ExecuteAsync_WithEqualSortOrder_ShouldThenOrderByDishIdAndIncludeHidden()
    {
        // STEP-01: Create restaurant as Domain.Entities.Restaurant and add a Dish to restaurant.Dishes.
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
        // STEP-02: Mock IRestaurantMenuRepository; stub repository.GetOwnerMenuAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetOwnerMenuAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);

        // STEP-03: Run CreateUseCase().ExecuteAsync and keep result.
        var result = await CreateUseCase(repository).ExecuteAsync(CancellationToken.None);

        // STEP-04: Assert result.Dishes.Select(filtered) equals 10 and result.Dishes[1].Status is "hidden".
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
