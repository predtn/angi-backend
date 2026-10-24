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
        var world = new ModerationTestContext();
        var dish = new Dish { Id = 105, RestaurantId = 7, Status = before };
        world.Repository.Setup(x => x.GetDishRestaurantIdAsync(105, It.IsAny<CancellationToken>())).ReturnsAsync(7);
        world.Repository.Setup(x => x.LockDishAsync(105, It.IsAny<CancellationToken>())).ReturnsAsync(dish);
        var result = await Create(world).ExecuteAsync(105, new() { Status = requested, Reason = "reason" }, default);
        result.Status.Should().Be(requested);
        dish.Status.Should().Be(expected);
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Removed dishes cannot be restored or hidden through moderation.
    /// <summary>Protects menu-removal semantics from accidental resurrection.</summary>
    [Theory]
    [InlineData("active")]
    [InlineData("hidden")]
    public async Task RejectsRemovedDish(string status)
    {
        var world = new ModerationTestContext();
        world.Repository.Setup(x => x.GetDishRestaurantIdAsync(105, It.IsAny<CancellationToken>())).ReturnsAsync(7);
        world.Repository.Setup(x => x.LockDishAsync(105, It.IsAny<CancellationToken>())).ReturnsAsync(new Dish { Status = DishStatus.Removed });
        var action = () => Create(world).ExecuteAsync(105, new() { Status = status, Reason = "reason" }, default);
        (await action.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("DISH_REMOVED");
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Missing dishes return DISH_NOT_FOUND before starting a transaction.
    /// <summary>Checks that a missing resource has no write effects.</summary>
    [Fact]
    public async Task RejectsMissingDish()
    {
        var world = new ModerationTestContext();
        var action = () => Create(world).ExecuteAsync(404, new() { Status = "hidden", Reason = "reason" }, default);
        (await action.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("DISH_NOT_FOUND");
        world.UnitOfWork.VerifyNoOtherCalls();
    }
}
