using ANGI.Application.Common.Exceptions;
using ANGI.Application.UseCases.Moderation.UpdateDishModerationStatus;
using ANGI.Application.UseCases.Validators.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.UpdateDishModerationStatus;

/// <summary>Verifies dish status boundaries without manually creating reco outbox messages.</summary>
public sealed class UpdateDishModerationStatusUseCaseTests
{
    /// <summary>Builds the dish moderation use case using its production validator.</summary>
    private static UpdateDishModerationStatusUseCase Create(ModerationTestContext world) => new(
        new UpdateDishModerationStatusRequestDtoValidator(), world.Access, world.Repository.Object, world.UnitOfWork.Object);

    // TEST-01: Active and hidden dishes can transition or repeat their current state with one save.
    /// <summary>Covers every allowed source and destination state for MOD-R4.</summary>
    [Theory]
    [InlineData(DishStatus.Active, "hidden", DishStatus.Hidden)]
    [InlineData(DishStatus.Hidden, "active", DishStatus.Active)]
    [InlineData(DishStatus.Active, "active", DishStatus.Active)]
    [InlineData(DishStatus.Hidden, "hidden", DishStatus.Hidden)]
    public async Task SavesAllowedState(DishStatus before, string requested, DishStatus expected)
    {
        // STEP-01: Create world as ModerationTestContext and create dish as Dish.
        var world = new ModerationTestContext();
        var dish = new Dish { Id = 105, RestaurantId = 7, Status = before };
        // STEP-02: Stub world.GetDishRestaurantIdAsync and world.LockDishAsync.
        world.Repository.Setup(x => x.GetDishRestaurantIdAsync(105, It.IsAny<CancellationToken>())).ReturnsAsync(7);
        world.Repository.Setup(x => x.LockDishAsync(105, It.IsAny<CancellationToken>())).ReturnsAsync(dish);
        // STEP-03: Run Create().ExecuteAsync and keep result.
        var result = await Create(world).ExecuteAsync(105, new() { Status = requested, Reason = "reason" }, default);
        // STEP-04: Assert result.Status is requested and dish.Status is expected.
        result.Status.Should().Be(requested);
        dish.Status.Should().Be(expected);
        // STEP-05: Verify SaveChangesAsync is called once.
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Removed dishes cannot be restored or hidden through moderation.
    /// <summary>Protects menu-removal semantics from accidental resurrection.</summary>
    [Theory]
    [InlineData("active")]
    [InlineData("hidden")]
    public async Task RejectsRemovedDish(string status)
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Stub world.GetDishRestaurantIdAsync and world.LockDishAsync.
        world.Repository.Setup(x => x.GetDishRestaurantIdAsync(105, It.IsAny<CancellationToken>())).ReturnsAsync(7);
        world.Repository.Setup(x => x.LockDishAsync(105, It.IsAny<CancellationToken>())).ReturnsAsync(new Dish { Status = DishStatus.Removed });
        // STEP-03: Wrap Create().ExecuteAsync in action so its outcome can be asserted.
        var action = () => Create(world).ExecuteAsync(105, new() { Status = status, Reason = "reason" }, default);
        // STEP-04: Assert action throws ConflictException and the thrown exception.ErrorCode is "DISH_REMOVED".
        (await action.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("DISH_REMOVED");
        // STEP-05: Verify SaveChangesAsync is never called.
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Missing dishes return DISH_NOT_FOUND before starting a transaction.
    /// <summary>Checks that a missing resource has no write effects.</summary>
    [Fact]
    public async Task RejectsMissingDish()
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Wrap Create().ExecuteAsync in action so its outcome can be asserted.
        var action = () => Create(world).ExecuteAsync(404, new() { Status = "hidden", Reason = "reason" }, default);
        // STEP-03: Assert action throws NotFoundException and the thrown exception.ErrorCode is "DISH_NOT_FOUND".
        (await action.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("DISH_NOT_FOUND");
        // STEP-04: Verify world receives no other calls.
        world.UnitOfWork.VerifyNoOtherCalls();
    }
}
