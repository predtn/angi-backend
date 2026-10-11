using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.UpdateDishAvailability;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using FluentAssertions;
using Moq;

namespace ANGI.Test.Application.UseCases.Restaurant.UpdateDishAvailability;

public sealed class UpdateDishAvailabilityUseCaseTests
{
    // TEST-01: Persist the requested availability only for a dish owned by the caller.
    /// <summary>Verifies the successful OWN-10 direct availability update.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOwnedDish_ShouldPersistAvailability()
    {
        var dish = new Dish { Id = 105, IsAvailable = true };
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetTrackedOwnerDishAsync(12, 105, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dish);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var useCase = CreateUseCase(repository, unitOfWork);

        var result = await useCase.ExecuteAsync(
            105,
            new UpdateDishAvailabilityRequestDto { IsAvailable = false },
            CancellationToken.None);

        dish.IsAvailable.Should().BeFalse();
        result.Should().BeEquivalentTo(new DishAvailabilityDto { DishId = 105, IsAvailable = false });
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Switch availability in both true-to-false and false-to-true directions.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExecuteAsync_ShouldSwitchAvailabilityBothDirections(bool current, bool requested)
    {
        var dish = new Dish { Id = 105, IsAvailable = current };
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetTrackedOwnerDishAsync(12, 105, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dish);
        var unitOfWork = new Mock<IUnitOfWork>();

        var result = await CreateUseCase(repository, unitOfWork).ExecuteAsync(
            105,
            new UpdateDishAvailabilityRequestDto { IsAvailable = requested },
            CancellationToken.None);

        result.IsAvailable.Should().Be(requested);
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Reject removed or other-owner dishes without persisting.
    [Fact]
    public async Task ExecuteAsync_WithRemovedOrOtherOwnerDish_ShouldNotPersist()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetTrackedOwnerDishAsync(12, 105, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Dish?)null);
        var unitOfWork = new Mock<IUnitOfWork>();

        var act = () => CreateUseCase(repository, unitOfWork).ExecuteAsync(
            105,
            new UpdateDishAvailabilityRequestDto { IsAvailable = false },
            CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Hide ownership failures behind the documented dish-not-found response.
    /// <summary>Verifies OWN-10 does not expose dishes belonging to another restaurant.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnknownOwnedDish_ShouldThrowNotFound()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetTrackedOwnerDishAsync(12, 105, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Dish?)null);
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = CreateUseCase(repository, unitOfWork);

        var act = () => useCase.ExecuteAsync(
            105,
            new UpdateDishAvailabilityRequestDto { IsAvailable = false },
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("DISH_NOT_FOUND");
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: Validate the body before performing an ownership lookup.
    /// <summary>Verifies an omitted isAvailable value produces validation failure first.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutAvailability_ShouldValidateFirst()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        var useCase = CreateUseCase(repository, new Mock<IUnitOfWork>());

        var act = () => useCase.ExecuteAsync(
            105,
            new UpdateDishAvailabilityRequestDto(),
            CancellationToken.None);

        await act.Should().ThrowAsync<FluentValidation.ValidationException>();
        repository.Verify(item => item.GetTrackedOwnerDishAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Creates the use case with the production validator and an authenticated owner.</summary>
    private static UpdateDishAvailabilityUseCase CreateUseCase(
        Mock<IRestaurantMenuRepository> repository,
        Mock<IUnitOfWork> unitOfWork)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(12);
        return new UpdateDishAvailabilityUseCase(
            new UpdateDishAvailabilityRequestDtoValidator(),
            repository.Object,
            currentUser.Object,
            unitOfWork.Object);
    }
}
