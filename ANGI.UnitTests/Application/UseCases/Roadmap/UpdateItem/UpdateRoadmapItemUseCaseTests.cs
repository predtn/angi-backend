using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Roadmap.UpdateItem;
using ANGI.Application.UseCases.Validators.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Roadmap.UpdateItem;

/// <summary>Verifies UpdateRoadmapItemUseCase edits schedule fields in place and replaces dishes atomically.</summary>
public sealed class UpdateRoadmapItemUseCaseTests
{
    private const int TravelerId = 12;
    private const long RoadmapId = 88;
    private const long ItemId = 1502;
    private const long CurrentDayId = 301;
    private const long DestinationDayId = 302;

    // TEST-01: Update schedule fields in place while preserving item identity and feedback.
    /// <summary>Verifies the non-replacement RM-08 path mutates once and maps the caller's existing feedback.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutDishId_ShouldUpdateExistingItemAndPreserveFeedback()
    {
        // STEP-01: Create fixture through CreateFixture.
        var fixture = CreateFixture();
        // STEP-02: Stub fixture.GetFeedbackByItemIdsAsync.
        fixture.RoadmapRepository.Setup(repository => repository.GetFeedbackByItemIdsAsync(
                TravelerId, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, FeedbackValue> { [ItemId] = FeedbackValue.Like });

        // STEP-03: Run fixture.UseCase.ExecuteAsync with RoadmapId, ItemId and UpdateRoadmapItemRequestDto { DayId, MealSlot, SortOrder } and keep result.
        var result = await fixture.UseCase.ExecuteAsync(RoadmapId, ItemId, new UpdateRoadmapItemRequestDto
        {
            DayId = DestinationDayId,
            MealSlot = "late_night",
            SortOrder = short.MaxValue
        }, CancellationToken.None);

        // STEP-04: Assert result.Id is ItemId, result.DayId is DestinationDayId, result.DayNumber is 2, result.MealSlot is "late_night" and 4 more.
        result.Id.Should().Be(ItemId);
        result.DayId.Should().Be(DestinationDayId);
        result.DayNumber.Should().Be(2);
        result.MealSlot.Should().Be("late_night");
        result.SortOrder.Should().Be(short.MaxValue);
        result.Source.Should().Be("ai");
        result.RecoRequestId.Should().Be(fixture.OriginalRecoRequestId);
        result.MyFeedback.Should().Be("like");
        // STEP-05: Verify RemoveItem is never called, AddItem is never called, SaveChangesAsync is called once and CommitAsync is called once.
        fixture.RoadmapRepository.Verify(repository => repository.RemoveItem(It.IsAny<RoadmapItem>()), Times.Never);
        fixture.RoadmapRepository.Verify(repository => repository.AddItem(It.IsAny<RoadmapItem>()), Times.Never);
        fixture.UnitOfWork.Verify(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        fixture.Transaction.Verify(transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Replace a dish by deleting the old item and inserting a new manual item atomically.
    /// <summary>Verifies RM-08 returns a new id and carries the requested destination, order, and reco request id.</summary>
    [Fact]
    public async Task ExecuteAsync_WithDishId_ShouldReplaceItemAndReturnNewIdentity()
    {
        // STEP-01: Create fixture through CreateFixture and create requestId through Guid.NewGuid.
        var fixture = CreateFixture();
        var requestId = Guid.NewGuid();
        // STEP-02: Declare replacement to capture a call argument; stub fixture.AddItem.
        RoadmapItem? replacement = null;
        fixture.RoadmapRepository.Setup(repository => repository.AddItem(It.IsAny<RoadmapItem>()))
            .Callback<RoadmapItem>(item =>
            {
                item.Id = 1600;
                item.CreatedAt = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
                replacement = item;
            });

        // STEP-03: Run fixture.UseCase.ExecuteAsync with RoadmapId, ItemId and UpdateRoadmapItemRequestDto { DishId, RecoRequestId, DayId, ... } and keep result.
        var result = await fixture.UseCase.ExecuteAsync(RoadmapId, ItemId, new UpdateRoadmapItemRequestDto
        {
            DishId = 120,
            RecoRequestId = requestId,
            DayId = DestinationDayId,
            MealSlot = "dinner",
            SortOrder = 1
        }, CancellationToken.None);

        // STEP-04: Assert replacement is not null, replacement.Id is 1600, replacement.RoadmapDayId is DestinationDayId, replacement.DishId is 120 and 2 more.
        replacement.Should().NotBeNull();
        replacement!.Id.Should().Be(1600);
        replacement.RoadmapDayId.Should().Be(DestinationDayId);
        replacement.DishId.Should().Be(120);
        replacement.Source.Should().Be(RoadmapItemSource.Manual);
        replacement.RecoRequestId.Should().Be(requestId);
        // STEP-05: Assert result.Id is 1600, result.Dish.Id is 120, result.DayId is DestinationDayId, result.MealSlot is "dinner" and 1 more.
        result.Id.Should().Be(1600);
        result.Dish.Id.Should().Be(120);
        result.DayId.Should().Be(DestinationDayId);
        result.MealSlot.Should().Be("dinner");
        result.MyFeedback.Should().BeNull();
        // STEP-06: Verify RemoveItem is called once and AddItem is called once.
        fixture.RoadmapRepository.Verify(repository => repository.RemoveItem(fixture.CurrentItem), Times.Once);
        fixture.RoadmapRepository.Verify(repository => repository.AddItem(replacement), Times.Once);
        // STEP-07: Verify GetFeedbackByItemIdsAsync is never called.
        fixture.RoadmapRepository.Verify(repository => repository.GetFeedbackByItemIdsAsync(
            It.IsAny<int>(), It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Treat a supplied dishId as replacement even when it equals the current dish.
    /// <summary>Verifies the API rule that presence of dishId always isolates prior item feedback behind a new id.</summary>
    [Fact]
    public async Task ExecuteAsync_WithSameDishId_ShouldStillCreateReplacement()
    {
        // STEP-01: Create fixture through CreateFixture.
        var fixture = CreateFixture(replacementDish: PublicDish(105));

        // STEP-02: Run fixture.UseCase.ExecuteAsync with RoadmapId, ItemId and UpdateRoadmapItemRequestDto { DishId = 105 }.
        await fixture.UseCase.ExecuteAsync(RoadmapId, ItemId,
            new UpdateRoadmapItemRequestDto { DishId = 105 }, CancellationToken.None);

        // STEP-03: Verify RemoveItem is called once and AddItem is called once.
        fixture.RoadmapRepository.Verify(repository => repository.RemoveItem(fixture.CurrentItem), Times.Once);
        fixture.RoadmapRepository.Verify(repository => repository.AddItem(It.Is<RoadmapItem>(item =>
            item != fixture.CurrentItem && item.DishId == 105)), Times.Once);
    }

    // TEST-04: Preserve every omitted schedule field during an empty patch.
    /// <summary>Verifies RM-08 omission semantics do not silently reset day, meal, order, source, or reco metadata.</summary>
    [Fact]
    public async Task ExecuteAsync_WithEmptyPatch_ShouldPreserveExistingValues()
    {
        // STEP-01: Create fixture through CreateFixture.
        var fixture = CreateFixture();

        // STEP-02: Run fixture.UseCase.ExecuteAsync with RoadmapId, ItemId and an empty UpdateRoadmapItemRequestDto and keep result.
        var result = await fixture.UseCase.ExecuteAsync(
            RoadmapId, ItemId, new UpdateRoadmapItemRequestDto(), CancellationToken.None);

        // STEP-03: Assert result.Id is ItemId, result.DayId is CurrentDayId, result.MealSlot is "breakfast", result.SortOrder is 4 and 2 more.
        result.Id.Should().Be(ItemId);
        result.DayId.Should().Be(CurrentDayId);
        result.MealSlot.Should().Be("breakfast");
        result.SortOrder.Should().Be(4);
        result.Source.Should().Be("ai");
        result.RecoRequestId.Should().Be(fixture.OriginalRecoRequestId);
        // STEP-04: Verify GetOwnedDayAsync is never called.
        fixture.RoadmapRepository.Verify(repository => repository.GetOwnedDayAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: Reject every unavailable replacement-dish partition without writing.
    /// <summary>Verifies missing, sold-out, temporarily closed, and permanently closed dishes map to DISH_NOT_AVAILABLE.</summary>
    [Theory]
    [InlineData(false, true, RestaurantOperatingStatus.Open)]
    [InlineData(true, false, RestaurantOperatingStatus.Open)]
    [InlineData(true, true, RestaurantOperatingStatus.TemporarilyClosed)]
    [InlineData(true, true, RestaurantOperatingStatus.PermanentlyClosed)]
    public async Task ExecuteAsync_WithUnavailableReplacement_ShouldThrowDishNotAvailable(
        bool exists,
        bool isAvailable,
        RestaurantOperatingStatus operatingStatus)
    {
        // STEP-01: Derive dish from exists ? PublicDish(120) : null and 2 more.
        var dish = exists ? PublicDish(120) : null;
        if (dish is not null)
        {
            dish.IsAvailable = isAvailable;
            dish.Restaurant.OperatingStatus = operatingStatus;
        }

        var fixture = CreateFixture(replacementDish: dish);
        // STEP-02: Stub fixture.GetPublicDetailAsync.
        fixture.DishRepository.Setup(repository => repository.GetPublicDetailAsync(
                120, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dish);
        // STEP-03: Wrap fixture.UseCase.ExecuteAsync with RoadmapId, ItemId and UpdateRoadmapItemRequestDto { DishId = 120 } in act so its outcome can be asserted.
        var act = () => fixture.UseCase.ExecuteAsync(RoadmapId, ItemId,
            new UpdateRoadmapItemRequestDto { DishId = 120 }, CancellationToken.None);

        // STEP-04: Assert act throws BadRequestException and the thrown exception.ErrorCode is "DISH_NOT_AVAILABLE".
        (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("DISH_NOT_AVAILABLE");
        // STEP-05: Verify RemoveItem is never called, SaveChangesAsync is never called and CommitAsync is never called.
        fixture.RoadmapRepository.Verify(repository => repository.RemoveItem(It.IsAny<RoadmapItem>()), Times.Never);
        fixture.UnitOfWork.Verify(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        fixture.Transaction.Verify(transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-06: Hide missing, foreign, and roadmap-mismatched items behind ROADMAP_ITEM_NOT_FOUND.
    /// <summary>Verifies the ownership-scoped repository miss stops RM-08 before destination or dish reads.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenItemIsNotOwned_ShouldThrowRoadmapItemNotFound()
    {
        // STEP-01: Create fixture through CreateFixture.
        var fixture = CreateFixture(itemExists: false);
        // STEP-02: Wrap fixture.UseCase.ExecuteAsync with RoadmapId, ItemId and UpdateRoadmapItemRequestDto { DishId = 120 } in act so its outcome can be asserted.
        var act = () => fixture.UseCase.ExecuteAsync(RoadmapId, ItemId,
            new UpdateRoadmapItemRequestDto { DishId = 120 }, CancellationToken.None);

        // STEP-03: Assert act throws NotFoundException and the thrown exception.ErrorCode is "ROADMAP_ITEM_NOT_FOUND".
        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("ROADMAP_ITEM_NOT_FOUND");
        // STEP-04: Verify fixture receives no other calls and SaveChangesAsync is never called.
        fixture.DishRepository.VerifyNoOtherCalls();
        fixture.UnitOfWork.Verify(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-07: Reject a destination day outside the owned roadmap with the documented RM-08 not-found code.
    /// <summary>Verifies day membership is checked before replacement-dish lookup and no cross-roadmap move occurs.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenDestinationDayIsNotOwned_ShouldThrowRoadmapItemNotFound()
    {
        // STEP-01: Create fixture through CreateFixture.
        var fixture = CreateFixture(destinationExists: false);
        // STEP-02: Wrap fixture.UseCase.ExecuteAsync with RoadmapId, ItemId and UpdateRoadmapItemRequestDto { DayId = DestinationDayId, DishId = 120 } in act so its outcome can be asserted.
        var act = () => fixture.UseCase.ExecuteAsync(RoadmapId, ItemId,
            new UpdateRoadmapItemRequestDto { DayId = DestinationDayId, DishId = 120 }, CancellationToken.None);

        // STEP-03: Assert act throws NotFoundException and the thrown exception.ErrorCode is "ROADMAP_ITEM_NOT_FOUND".
        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("ROADMAP_ITEM_NOT_FOUND");
        // STEP-04: Verify fixture receives no other calls and RemoveItem is never called.
        fixture.DishRepository.VerifyNoOtherCalls();
        fixture.RoadmapRepository.Verify(repository => repository.RemoveItem(It.IsAny<RoadmapItem>()), Times.Never);
    }

    // TEST-08: Reject unauthenticated calls before opening a transaction.
    /// <summary>Verifies Application independently enforces RM-08 authentication.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        // STEP-01: Create fixture through CreateFixture.
        var fixture = CreateFixture(userId: null);
        // STEP-02: Wrap fixture.UseCase.ExecuteAsync with RoadmapId, ItemId and an empty UpdateRoadmapItemRequestDto in act so its outcome can be asserted.
        var act = () => fixture.UseCase.ExecuteAsync(
            RoadmapId, ItemId, new UpdateRoadmapItemRequestDto(), CancellationToken.None);

        // STEP-03: Assert act throws UnauthorizedException and the thrown exception.ErrorCode is "UNAUTHORIZED".
        (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
        // STEP-04: Verify BeginTransactionAsync is never called.
        fixture.UnitOfWork.Verify(unit => unit.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-09: Validate before identity, transactions, repositories, or writes.
    /// <summary>Verifies the coding-rule validation-first invariant for every invalid RM-08 value.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidRequest_ShouldThrowValidationFirst()
    {
        // STEP-01: Create fixture through CreateFixture.
        var fixture = CreateFixture();
        // STEP-02: Wrap fixture.UseCase.ExecuteAsync with RoadmapId, ItemId and UpdateRoadmapItemRequestDto { DishId = 0, DayId = 0, MealSlot = "brunch" } in act so its outcome can be asserted.
        var act = () => fixture.UseCase.ExecuteAsync(RoadmapId, ItemId,
            new UpdateRoadmapItemRequestDto { DishId = 0, DayId = 0, MealSlot = "brunch" },
            CancellationToken.None);

        // STEP-03: Assert act throws ValidationException.
        await act.Should().ThrowAsync<ValidationException>();
        // STEP-04: Verify BeginTransactionAsync is never called and fixture receives no other calls.
        fixture.UnitOfWork.Verify(unit => unit.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        fixture.RoadmapRepository.VerifyNoOtherCalls();
        fixture.DishRepository.VerifyNoOtherCalls();
    }

    /// <summary>Creates RM-08 with deterministic identity, persistence, and transaction boundaries.</summary>
    private static Fixture CreateFixture(
        bool itemExists = true,
        bool destinationExists = true,
        Dish? replacementDish = null,
        int? userId = TravelerId)
    {
        var originalRecoRequestId = Guid.NewGuid();
        var currentDay = new RoadmapDay { Id = CurrentDayId, RoadmapId = RoadmapId, DayNumber = 1 };
        var currentItem = new RoadmapItem
        {
            Id = ItemId,
            RoadmapDayId = CurrentDayId,
            RoadmapDay = currentDay,
            DishId = 105,
            Dish = PublicDish(105),
            MealSlot = MealSlot.Breakfast,
            SortOrder = 4,
            Source = RoadmapItemSource.Ai,
            RecoRequestId = originalRecoRequestId,
            CreatedAt = new DateTime(2026, 10, 2, 2, 10, 0, DateTimeKind.Utc)
        };
        var destinationDay = new RoadmapDay { Id = DestinationDayId, RoadmapId = RoadmapId, DayNumber = 2 };
        var roadmapRepository = new Mock<IRoadmapRepository>();
        roadmapRepository.Setup(repository => repository.LockAndGetOwnedItemAsync(
                RoadmapId, ItemId, TravelerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(itemExists ? currentItem : null);
        roadmapRepository.Setup(repository => repository.GetOwnedDayAsync(
                RoadmapId, DestinationDayId, TravelerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(destinationExists ? destinationDay : null);
        roadmapRepository.Setup(repository => repository.GetFeedbackByItemIdsAsync(
                TravelerId, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, FeedbackValue>());
        var dishRepository = new Mock<IDishRepository>();
        dishRepository.Setup(repository => repository.GetPublicDetailAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(replacementDish ?? PublicDish(120));
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(service => service.UserId).Returns(userId);
        var transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(unit => unit.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        var useCase = new UpdateRoadmapItemUseCase(
            new UpdateRoadmapItemRequestDtoValidator(),
            roadmapRepository.Object,
            dishRepository.Object,
            currentUser.Object,
            unitOfWork.Object);
        return new Fixture(
            useCase,
            currentItem,
            originalRecoRequestId,
            roadmapRepository,
            dishRepository,
            unitOfWork,
            transaction);
    }

    /// <summary>Builds an active and available public dish graph sufficient for RoadmapItem mapping.</summary>
    private static Dish PublicDish(int id) => new()
    {
        Id = id,
        Name = $"Dish {id}",
        Price = 45_000,
        DishForm = DishForm.Soup,
        IsAvailable = true,
        Status = DishStatus.Active,
        Restaurant = new Domain.Entities.Restaurant
        {
            Id = 7,
            Name = "Restaurant",
            ProvinceName = "Đà Nẵng",
            OperatingStatus = RestaurantOperatingStatus.Open
        }
    };

    /// <summary>Groups the use case under test with the current item and the mocks each scenario verifies.</summary>
    private sealed record Fixture(
        UpdateRoadmapItemUseCase UseCase,
        RoadmapItem CurrentItem,
        Guid OriginalRecoRequestId,
        Mock<IRoadmapRepository> RoadmapRepository,
        Mock<IDishRepository> DishRepository,
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IUnitOfWorkTransaction> Transaction);
}
