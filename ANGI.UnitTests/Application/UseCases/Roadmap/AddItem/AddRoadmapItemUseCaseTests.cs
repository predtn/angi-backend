using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Roadmap.AddItem;
using ANGI.Application.UseCases.Validators.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Roadmap.AddItem;

public sealed class AddRoadmapItemUseCaseTests
{
    private const int TravelerId = 12;
    private const long RoadmapId = 88;
    private const long DayId = 301;

    // TEST-01: Persist a manually sourced item with explicit order and reco request id exactly once.
    /// <summary>Verifies the principal atomic RM-07 insertion and complete RoadmapItem mapping path.</summary>
    [Fact]
    public async Task ExecuteAsync_WithExplicitOrder_ShouldAddCommitAndMapItem()
    {
        var dish = PublicDish();
        var fixture = CreateFixture((true, Day()), dish);
        var requestId = Guid.NewGuid();
        RoadmapItem? captured = null;
        fixture.RoadmapRepository.Setup(item => item.AddItem(It.IsAny<RoadmapItem>()))
            .Callback<RoadmapItem>(item =>
            {
                item.Id = 1502;
                item.CreatedAt = new DateTime(2026, 10, 2, 2, 10, 0, DateTimeKind.Utc);
                captured = item;
            });

        var result = await fixture.UseCase.ExecuteAsync(RoadmapId, DayId, new AddRoadmapItemRequestDto
        {
            DishId = dish.Id,
            MealSlot = "late_night",
            SortOrder = 7,
            RecoRequestId = requestId
        }, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.RoadmapDayId.Should().Be(DayId);
        captured.MealSlot.Should().Be(MealSlot.LateNight);
        captured.SortOrder.Should().Be(7);
        captured.Source.Should().Be(RoadmapItemSource.Manual);
        captured.RecoRequestId.Should().Be(requestId);
        result.Id.Should().Be(1502);
        result.DayId.Should().Be(DayId);
        result.DayNumber.Should().Be(1);
        result.MealSlot.Should().Be("late_night");
        result.Source.Should().Be("manual");
        result.Dish.Status.Should().Be("active");
        result.Dish.Rank.Should().BeNull();
        result.MyFeedback.Should().BeNull();
        fixture.UnitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        fixture.Transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Append omitted sortOrder after the meal, including empty and smallint-ceiling regions.
    /// <summary>Verifies RM-07's documented end-of-meal order calculation over all boundary regions.</summary>
    [Theory]
    [InlineData(null, 0)]
    [InlineData((short)4, 5)]
    [InlineData(short.MaxValue, short.MaxValue)]
    public async Task ExecuteAsync_WithoutSortOrder_ShouldAppendToMeal(short? existingOrder, short expected)
    {
        var day = Day();
        if (existingOrder.HasValue)
        {
            day.Items.Add(new RoadmapItem { MealSlot = MealSlot.Lunch, SortOrder = existingOrder.Value });
            day.Items.Add(new RoadmapItem { MealSlot = MealSlot.Dinner, SortOrder = short.MaxValue });
        }

        var fixture = CreateFixture((true, day), PublicDish());
        RoadmapItem? captured = null;
        fixture.RoadmapRepository.Setup(item => item.AddItem(It.IsAny<RoadmapItem>()))
            .Callback<RoadmapItem>(item => captured = item);

        await fixture.UseCase.ExecuteAsync(RoadmapId, DayId, new AddRoadmapItemRequestDto
        {
            DishId = 105,
            MealSlot = "lunch"
        }, CancellationToken.None);

        captured!.SortOrder.Should().Be(expected);
    }

    // TEST-03: Hide a missing or foreign roadmap behind ROADMAP_NOT_FOUND.
    /// <summary>Verifies RM-07 stops before dish lookup and persistence when ownership fails.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenRoadmapDoesNotExist_ShouldThrowRoadmapNotFound()
    {
        var fixture = CreateFixture((false, null), PublicDish());

        var act = () => fixture.UseCase.ExecuteAsync(RoadmapId, DayId, ValidRequest(), CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("ROADMAP_NOT_FOUND");
        fixture.DishRepository.VerifyNoOtherCalls();
        fixture.UnitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        fixture.Transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Return DAY_NOT_FOUND when the roadmap exists but the day does not belong to it.
    /// <summary>Verifies RM-07 distinguishes its two documented missing-resource errors.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenDayDoesNotBelongToRoadmap_ShouldThrowDayNotFound()
    {
        var fixture = CreateFixture((true, null), PublicDish());

        var act = () => fixture.UseCase.ExecuteAsync(RoadmapId, DayId, ValidRequest(), CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("DAY_NOT_FOUND");
        fixture.DishRepository.VerifyNoOtherCalls();
    }

    // TEST-05: Return DISH_NOT_AVAILABLE for absent and sold-out dishes.
    /// <summary>Verifies both unavailable-dish regions produce the documented 400 error without writing.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task ExecuteAsync_WhenDishIsNotPublicAndAvailable_ShouldThrowDishNotAvailable(
        bool dishExists,
        bool isAvailable)
    {
        var dish = dishExists ? PublicDish() : null;
        if (dish is not null)
        {
            dish.IsAvailable = isAvailable;
        }

        var fixture = CreateFixture((true, Day()), dish);
        var act = () => fixture.UseCase.ExecuteAsync(RoadmapId, DayId, ValidRequest(), CancellationToken.None);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("DISH_NOT_AVAILABLE");
        fixture.RoadmapRepository.Verify(item => item.AddItem(It.IsAny<RoadmapItem>()), Times.Never);
        fixture.UnitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-06: Reject a dish when its restaurant stopped operating after the suggestion was returned.
    /// <summary>Verifies RM-07 rechecks restaurant availability and does not persist stale RM-06 choices.</summary>
    [Theory]
    [InlineData(RestaurantOperatingStatus.TemporarilyClosed)]
    [InlineData(RestaurantOperatingStatus.PermanentlyClosed)]
    public async Task ExecuteAsync_WhenRestaurantIsNotOpen_ShouldThrowDishNotAvailable(
        RestaurantOperatingStatus operatingStatus)
    {
        var dish = PublicDish();
        dish.Restaurant.OperatingStatus = operatingStatus;
        var fixture = CreateFixture((true, Day()), dish);

        var act = () => fixture.UseCase.ExecuteAsync(RoadmapId, DayId, ValidRequest(), CancellationToken.None);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("DISH_NOT_AVAILABLE");
        fixture.RoadmapRepository.Verify(item => item.AddItem(It.IsAny<RoadmapItem>()), Times.Never);
        fixture.UnitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        fixture.Transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-07: Reject an unauthenticated caller before starting a transaction.
    /// <summary>Verifies Application independently enforces RM-07 authentication.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        var fixture = CreateFixture((true, Day()), PublicDish(), userId: null);
        var act = () => fixture.UseCase.ExecuteAsync(RoadmapId, DayId, ValidRequest(), CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
        fixture.UnitOfWork.Verify(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-08: Validate before identity, transaction, repositories, or persistence.
    /// <summary>Verifies the coding-rule validation-first invariant for RM-07.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidRequest_ShouldThrowValidationFirst()
    {
        var fixture = CreateFixture((true, Day()), PublicDish());
        var act = () => fixture.UseCase.ExecuteAsync(RoadmapId, DayId,
            new AddRoadmapItemRequestDto { DishId = 0, MealSlot = "brunch" }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        fixture.UnitOfWork.Verify(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        fixture.RoadmapRepository.VerifyNoOtherCalls();
        fixture.DishRepository.VerifyNoOtherCalls();
    }

    /// <summary>Creates a valid RM-07 body for negative-path tests.</summary>
    private static AddRoadmapItemRequestDto ValidRequest() => new() { DishId = 105, MealSlot = "lunch" };

    /// <summary>Builds an owned day with the documented day number.</summary>
    private static RoadmapDay Day() => new() { Id = DayId, RoadmapId = RoadmapId, DayNumber = 1 };

    /// <summary>Builds a public, available dish graph sufficient for RoadmapItem mapping.</summary>
    private static Dish PublicDish() => new()
    {
        Id = 105,
        Name = "Bún bò Huế",
        Price = 45_000,
        DishForm = DishForm.Soup,
        IsAvailable = true,
        Status = DishStatus.Active,
        Restaurant = new Domain.Entities.Restaurant
        {
            Id = 7,
            Name = "Bún Bò Bà Diệu",
            ProvinceName = "Đà Nẵng"
        }
    };

    /// <summary>Creates RM-07 with deterministic identity, transaction, and repository boundaries.</summary>
    private static Fixture CreateFixture(
        (bool RoadmapExists, RoadmapDay? Day) target,
        Dish? dish,
        int? userId = TravelerId)
    {
        var roadmapRepository = new Mock<IRoadmapRepository>();
        roadmapRepository.Setup(item => item.LockAndGetOwnedDayAsync(
                RoadmapId, DayId, TravelerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(target);
        var dishRepository = new Mock<IDishRepository>();
        dishRepository.Setup(item => item.GetPublicDetailAsync(105, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dish);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(userId);
        var transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        var useCase = new AddRoadmapItemUseCase(
            new AddRoadmapItemRequestDtoValidator(),
            roadmapRepository.Object,
            dishRepository.Object,
            currentUser.Object,
            unitOfWork.Object);
        return new Fixture(useCase, roadmapRepository, dishRepository, unitOfWork, transaction);
    }

    private sealed record Fixture(
        AddRoadmapItemUseCase UseCase,
        Mock<IRoadmapRepository> RoadmapRepository,
        Mock<IDishRepository> DishRepository,
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IUnitOfWorkTransaction> Transaction);
}
