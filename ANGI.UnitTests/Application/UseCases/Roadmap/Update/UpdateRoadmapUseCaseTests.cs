using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Roadmap.Update;
using ANGI.Application.UseCases.Validators.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Roadmap.Update;

/// <summary>Verifies UpdateRoadmapUseCase resizes days safely and keeps origin data consistent with the origin mode.</summary>
public sealed class UpdateRoadmapUseCaseTests
{
    // TEST-01: Add consecutive empty trailing days when numDays increases.
    /// <summary>Verifies RM-04 grows the aggregate and commits it atomically.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenNumDaysIncreases_ShouldAddTrailingDaysAndCommit()
    {
        // STEP-01: Create roadmap through CreateRoadmap and create fixture through CreateFixture.
        var roadmap = CreateRoadmap(2);
        var fixture = CreateFixture(roadmap);

        // STEP-02: Run fixture.UseCase.ExecuteAsync with UpdateRoadmapRequestDto { NumDays = 4 } and keep result.
        var result = await fixture.UseCase.ExecuteAsync(
            roadmap.Id,
            new UpdateRoadmapRequestDto { NumDays = 4 },
            CancellationToken.None);

        // STEP-03: Assert roadmap.NumDays is 4, roadmap.Days.Select(filtered).Order() equals 1 and result.Itinerary has 4 item(s).
        roadmap.NumDays.Should().Be(4);
        roadmap.Days.Select(day => day.DayNumber).Order().Should().Equal(1, 2, 3, 4);
        result.Itinerary.Should().HaveCount(4);
        // STEP-04: Verify SaveChangesAsync is called once and CommitAsync is called once.
        fixture.UnitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        fixture.Transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Remove only empty trailing days when numDays decreases.
    /// <summary>Verifies RM-04 shrinks a roadmap without affecting retained days.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenTrailingDaysAreEmpty_ShouldRemoveThem()
    {
        // STEP-01: Create roadmap through CreateRoadmap and create fixture through CreateFixture.
        var roadmap = CreateRoadmap(4);
        var fixture = CreateFixture(roadmap);

        // STEP-02: Run fixture.UseCase.ExecuteAsync with UpdateRoadmapRequestDto { NumDays = 2 }.
        await fixture.UseCase.ExecuteAsync(
            roadmap.Id,
            new UpdateRoadmapRequestDto { NumDays = 2 },
            CancellationToken.None);

        // STEP-03: Assert roadmap.Days.Select(filtered).Order() equals 1 and roadmap.NumDays is 2.
        roadmap.Days.Select(day => day.DayNumber).Order().Should().Equal(1, 2);
        roadmap.NumDays.Should().Be(2);
    }

    // TEST-03: Reject shrinking when any removed day still contains an item.
    /// <summary>Verifies DAY_NOT_EMPTY protects planned dishes and rolls the transaction back by disposal.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenRemovedDayHasItem_ShouldThrowDayNotEmpty()
    {
        // STEP-01: Create roadmap through CreateRoadmap, add a RoadmapItem to roadmap.Days.Single(filtered).Items and create fixture through CreateFixture.
        var roadmap = CreateRoadmap(3);
        roadmap.Days.Single(day => day.DayNumber == 3).Items.Add(new RoadmapItem { Id = 91 });
        var fixture = CreateFixture(roadmap);

        // STEP-02: Wrap fixture.UseCase.ExecuteAsync with UpdateRoadmapRequestDto { NumDays = 2 } in act so its outcome can be asserted.
        var act = () => fixture.UseCase.ExecuteAsync(
            roadmap.Id,
            new UpdateRoadmapRequestDto { NumDays = 2 },
            CancellationToken.None);

        // STEP-03: Assert act throws ConflictException, the thrown exception.ErrorCode is "DAY_NOT_EMPTY" and roadmap.Days has 3 item(s).
        (await act.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("DAY_NOT_EMPTY");
        roadmap.Days.Should().HaveCount(3);
        // STEP-04: Verify SaveChangesAsync is never called and CommitAsync is never called.
        fixture.UnitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        fixture.Transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Hide a missing or foreign roadmap behind ROADMAP_NOT_FOUND.
    /// <summary>Verifies ownership is enforced by the repository lookup and public error contract.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenOwnedRoadmapDoesNotExist_ShouldThrowRoadmapNotFound()
    {
        // STEP-01: Create fixture through CreateFixture.
        var fixture = CreateFixture(null);

        // STEP-02: Wrap fixture.UseCase.ExecuteAsync with UpdateRoadmapRequestDto { Title = "New title" } in act so its outcome can be asserted.
        var act = () => fixture.UseCase.ExecuteAsync(
            404,
            new UpdateRoadmapRequestDto { Title = "New title" },
            CancellationToken.None);

        // STEP-03: Assert act throws NotFoundException and the thrown exception.ErrorCode is "ROADMAP_NOT_FOUND".
        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("ROADMAP_NOT_FOUND");
    }

    // TEST-05: Clear persisted pinned data when switching to none or gps.
    /// <summary>Verifies switching away from pinned restores the database origin invariant.</summary>
    [Theory]
    [InlineData("none", RoadmapOriginMode.None)]
    [InlineData("gps", RoadmapOriginMode.Gps)]
    public async Task ExecuteAsync_WhenSwitchingAwayFromPinned_ShouldClearStoredOrigin(
        string requestedMode,
        RoadmapOriginMode expectedMode)
    {
        // STEP-01: Create roadmap through CreateRoadmap, set roadmap.OriginMode to RoadmapOriginMode.Pinned, set roadmap.OriginLatitude to 10m and 3 more.
        var roadmap = CreateRoadmap(1);
        roadmap.OriginMode = RoadmapOriginMode.Pinned;
        roadmap.OriginLatitude = 10m;
        roadmap.OriginLongitude = 106m;
        roadmap.OriginLabel = "Hotel";
        var fixture = CreateFixture(roadmap);

        // STEP-02: Run fixture.UseCase.ExecuteAsync with UpdateRoadmapRequestDto { OriginMode = requestedMode }.
        await fixture.UseCase.ExecuteAsync(
            roadmap.Id,
            new UpdateRoadmapRequestDto { OriginMode = requestedMode },
            CancellationToken.None);

        // STEP-03: Assert roadmap.OriginMode is expectedMode, roadmap.OriginLatitude is null, roadmap.OriginLongitude is null and roadmap.OriginLabel is null.
        roadmap.OriginMode.Should().Be(expectedMode);
        roadmap.OriginLatitude.Should().BeNull();
        roadmap.OriginLongitude.Should().BeNull();
        roadmap.OriginLabel.Should().BeNull();
    }

    // TEST-06: Allow one pinned coordinate to change while retaining the other existing coordinate.
    /// <summary>Verifies partial coordinate updates resolve against the persisted pinned state.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenUpdatingOnePinnedCoordinate_ShouldRetainOtherCoordinate()
    {
        // STEP-01: Create roadmap through CreateRoadmap, set roadmap.OriginMode to RoadmapOriginMode.Pinned, set roadmap.OriginLatitude to 10m and 2 more.
        var roadmap = CreateRoadmap(1);
        roadmap.OriginMode = RoadmapOriginMode.Pinned;
        roadmap.OriginLatitude = 10m;
        roadmap.OriginLongitude = 106m;
        var fixture = CreateFixture(roadmap);

        // STEP-02: Run fixture.UseCase.ExecuteAsync with UpdateRoadmapRequestDto { OriginLatitude = 11m }.
        await fixture.UseCase.ExecuteAsync(
            roadmap.Id,
            new UpdateRoadmapRequestDto { OriginLatitude = 11m },
            CancellationToken.None);

        // STEP-03: Assert roadmap.OriginLatitude is 11m and roadmap.OriginLongitude is 106m.
        roadmap.OriginLatitude.Should().Be(11m);
        roadmap.OriginLongitude.Should().Be(106m);
    }

    // TEST-07: Reject switching to pinned without both effective coordinates.
    /// <summary>Verifies RM-04 cannot persist an invalid pinned origin.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenPinnedCoordinatesAreIncomplete_ShouldThrowValidationException()
    {
        // STEP-01: Create roadmap through CreateRoadmap and create fixture through CreateFixture.
        var roadmap = CreateRoadmap(1);
        var fixture = CreateFixture(roadmap);

        // STEP-02: Wrap fixture.UseCase.ExecuteAsync with UpdateRoadmapRequestDto { OriginMode = "pinned", OriginLatitude = 10m } in act so its outcome can be asserted.
        var act = () => fixture.UseCase.ExecuteAsync(
            roadmap.Id,
            new UpdateRoadmapRequestDto { OriginMode = "pinned", OriginLatitude = 10m },
            CancellationToken.None);

        // STEP-03: Assert act throws ValidationException and exception.Errors contains a matching item.
        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.Errors.Should().Contain(error => error.PropertyName == "OriginLongitude");
        // STEP-04: Verify SaveChangesAsync is never called.
        fixture.UnitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-08: Recalculate costs, remaining budget, dates, and caller feedback in the RM-04 response.
    /// <summary>Verifies RoadmapDetail is derived from current aggregate data rather than stale stored totals.</summary>
    [Fact]
    public async Task ExecuteAsync_WithExistingItems_ShouldMapCompleteCalculatedDetail()
    {
        // STEP-01: Create roadmap through CreateRoadmap, set roadmap.StartDate to a new DateOnly, set roadmap.BudgetAmount to 40_000 and 6 more.
        var roadmap = CreateRoadmap(1);
        roadmap.StartDate = new DateOnly(2026, 10, 10);
        roadmap.BudgetAmount = 40_000;
        var day = roadmap.Days.Single();
        var restaurant = new Domain.Entities.Restaurant { Id = 7, Name = "Restaurant", AddressLine = "Address" };
        var dish = new Dish
        {
            Id = 20,
            Name = "Dish",
            Price = 50_000,
            Restaurant = restaurant,
            DishForm = DishForm.Soup,
            Status = DishStatus.Active
        };
        var item = new RoadmapItem
        {
            Id = 31,
            RoadmapDay = day,
            RoadmapDayId = day.Id,
            Dish = dish,
            DishId = dish.Id,
            MealSlot = MealSlot.Lunch,
            Source = RoadmapItemSource.Manual
        };
        day.Items.Add(item);
        var fixture = CreateFixture(roadmap, new Dictionary<long, FeedbackValue> { [31] = FeedbackValue.Like });

        // STEP-02: Run fixture.UseCase.ExecuteAsync with an empty UpdateRoadmapRequestDto and keep result.
        var result = await fixture.UseCase.ExecuteAsync(
            roadmap.Id,
            new UpdateRoadmapRequestDto(),
            CancellationToken.None);

        // STEP-03: Assert result.Overview.TotalEstimatedCost is 50_000, result.Budget.Remaining is -10_000, result.Budget.IsOverBudget is true and 2 more.
        result.Overview.TotalEstimatedCost.Should().Be(50_000);
        result.Budget.Remaining.Should().Be(-10_000);
        result.Budget.IsOverBudget.Should().BeTrue();
        result.Itinerary.Single().Date.Should().Be(new DateOnly(2026, 10, 10));
        result.Itinerary.Single().Items.Single().MyFeedback.Should().Be("like");
    }

    /// <summary>Creates a contiguous roadmap aggregate with the requested number of empty days.</summary>
    private static Domain.Entities.Roadmap CreateRoadmap(short numDays)
    {
        var roadmap = new Domain.Entities.Roadmap
        {
            Id = 88,
            UserId = 12,
            Title = "Trip",
            NumDays = numDays,
            OriginMode = RoadmapOriginMode.None
        };
        for (short dayNumber = 1; dayNumber <= numDays; dayNumber++)
        {
            roadmap.Days.Add(new RoadmapDay
            {
                Id = 100 + dayNumber,
                DayNumber = dayNumber,
                Roadmap = roadmap
            });
        }

        return roadmap;
    }

    /// <summary>Creates RM-04 with deterministic repository, identity, and transaction boundaries.</summary>
    private static Fixture CreateFixture(
        Domain.Entities.Roadmap? roadmap,
        IReadOnlyDictionary<long, FeedbackValue>? feedback = null)
    {
        var repository = new Mock<IRoadmapRepository>();
        repository.Setup(item => item.LockAndGetTrackedAsync(It.IsAny<long>(), 12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(roadmap);
        repository.Setup(item => item.GetFeedbackByItemIdsAsync(12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(feedback ?? new Dictionary<long, FeedbackValue>());
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(12);
        var transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        var useCase = new UpdateRoadmapUseCase(
            new UpdateRoadmapRequestDtoValidator(),
            repository.Object,
            currentUser.Object,
            unitOfWork.Object);
        return new Fixture(useCase, unitOfWork, transaction);
    }

    /// <summary>Groups the use case under test with the unit-of-work and transaction mocks each scenario verifies.</summary>
    private sealed record Fixture(
        UpdateRoadmapUseCase UseCase,
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IUnitOfWorkTransaction> Transaction);
}
