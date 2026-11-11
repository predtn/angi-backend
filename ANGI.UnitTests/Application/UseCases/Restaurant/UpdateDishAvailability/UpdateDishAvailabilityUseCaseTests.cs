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

namespace ANGI.UnitTests.Application.UseCases.Restaurant.UpdateDishAvailability;

/// <summary>Verifies UpdateDishAvailabilityUseCase toggles availability only for dishes the caller owns.</summary>
public sealed class UpdateDishAvailabilityUseCaseTests
{
    // TEST-01: Persist the requested availability only for a dish owned by the caller.
    /// <summary>Verifies the successful OWN-10 direct availability update.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOwnedDish_ShouldPersistAvailability()
    {
        // STEP-01: Create dish as Dish.
        var dish = new Dish { Id = 105, IsAvailable = true };
        // STEP-02: Mock IRestaurantMenuRepository and IUnitOfWork; stub repository.GetTrackedOwnerDishAsync and unitOfWork.SaveChangesAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetTrackedOwnerDishAsync(12, 105, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dish);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, unitOfWork);

        // STEP-04: Run useCase.ExecuteAsync with UpdateDishAvailabilityRequestDto { IsAvailable = false } and keep result.
        var result = await useCase.ExecuteAsync(
            105,
            new UpdateDishAvailabilityRequestDto { IsAvailable = false },
            CancellationToken.None);

        // STEP-05: Assert dish.IsAvailable is false and result is equivalent to a new DishAvailabilityDto.
        dish.IsAvailable.Should().BeFalse();
        result.Should().BeEquivalentTo(new DishAvailabilityDto { DishId = 105, IsAvailable = false });
        // STEP-06: Verify SaveChangesAsync is called once.
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Switch availability in both true-to-false and false-to-true directions.
    /// <summary>Ensures availability can be turned both off and on.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExecuteAsync_ShouldSwitchAvailabilityBothDirections(bool current, bool requested)
    {
        // STEP-01: Create dish as Dish.
        var dish = new Dish { Id = 105, IsAvailable = current };
        // STEP-02: Mock IRestaurantMenuRepository and IUnitOfWork; stub repository.GetTrackedOwnerDishAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetTrackedOwnerDishAsync(12, 105, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dish);
        var unitOfWork = new Mock<IUnitOfWork>();

        // STEP-03: Run CreateUseCase().ExecuteAsync with UpdateDishAvailabilityRequestDto { IsAvailable = requested } and keep result.
        var result = await CreateUseCase(repository, unitOfWork).ExecuteAsync(
            105,
            new UpdateDishAvailabilityRequestDto { IsAvailable = requested },
            CancellationToken.None);

        // STEP-04: Assert result.IsAvailable is requested.
        result.IsAvailable.Should().Be(requested);
        // STEP-05: Verify SaveChangesAsync is called once.
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Reject removed or other-owner dishes without persisting.
    /// <summary>Ensures removed or foreign dishes are never changed.</summary>
    [Fact]
    public async Task ExecuteAsync_WithRemovedOrOtherOwnerDish_ShouldNotPersist()
    {
        // STEP-01: Mock IRestaurantMenuRepository and IUnitOfWork; stub repository.GetTrackedOwnerDishAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetTrackedOwnerDishAsync(12, 105, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Dish?)null);
        var unitOfWork = new Mock<IUnitOfWork>();

        // STEP-02: Wrap CreateUseCase().ExecuteAsync with UpdateDishAvailabilityRequestDto { IsAvailable = false } in act so its outcome can be asserted.
        var act = () => CreateUseCase(repository, unitOfWork).ExecuteAsync(
            105,
            new UpdateDishAvailabilityRequestDto { IsAvailable = false },
            CancellationToken.None);

        // STEP-03: Assert act throws NotFoundException.
        await act.Should().ThrowAsync<NotFoundException>();
        // STEP-04: Verify SaveChangesAsync is never called.
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Hide ownership failures behind the documented dish-not-found response.
    /// <summary>Verifies OWN-10 does not expose dishes belonging to another restaurant.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnknownOwnedDish_ShouldThrowNotFound()
    {
        // STEP-01: Mock IRestaurantMenuRepository and IUnitOfWork; stub repository.GetTrackedOwnerDishAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetTrackedOwnerDishAsync(12, 105, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Dish?)null);
        var unitOfWork = new Mock<IUnitOfWork>();
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, unitOfWork);

        // STEP-03: Wrap useCase.ExecuteAsync with UpdateDishAvailabilityRequestDto { IsAvailable = false } in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(
            105,
            new UpdateDishAvailabilityRequestDto { IsAvailable = false },
            CancellationToken.None);

        // STEP-04: Assert act throws NotFoundException and exception.ErrorCode is "DISH_NOT_FOUND".
        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("DISH_NOT_FOUND");
        // STEP-05: Verify SaveChangesAsync is never called.
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: Validate the body before performing an ownership lookup.
    /// <summary>Verifies an omitted isAvailable value produces validation failure first.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutAvailability_ShouldValidateFirst()
    {
        // STEP-01: Mock IRestaurantMenuRepository.
        var repository = new Mock<IRestaurantMenuRepository>();
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, new Mock<IUnitOfWork>());

        // STEP-03: Wrap useCase.ExecuteAsync with an empty UpdateDishAvailabilityRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(
            105,
            new UpdateDishAvailabilityRequestDto(),
            CancellationToken.None);

        // STEP-04: Assert act throws FluentValidation.ValidationException.
        await act.Should().ThrowAsync<FluentValidation.ValidationException>();
        // STEP-05: Verify GetTrackedOwnerDishAsync is never called.
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
