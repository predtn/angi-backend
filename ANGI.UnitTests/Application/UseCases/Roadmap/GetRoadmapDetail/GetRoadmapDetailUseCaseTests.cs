using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Roadmap.GetRoadmapDetail;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Roadmap.GetRoadmapDetail;

public sealed class GetRoadmapDetailUseCaseTests
{
    // TEST-01: Map every RM-03 section from current aggregate data, including a removed dish and item feedback.
    /// <summary>Verifies ownership lookup, calculated detail mapping, deterministic ordering, and feedback loading.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOwnedRoadmap_ShouldReturnCompleteCalculatedDetail()
    {
        var roadmap = CreateRoadmapWithItems();
        var repository = RepositoryReturning(roadmap, new Dictionary<long, FeedbackValue>
        {
            [501] = FeedbackValue.Dislike
        });
        var useCase = CreateUseCase(repository.Object, userId: 12);

        var result = await useCase.ExecuteAsync(88, CancellationToken.None);

        result.Overview.Id.Should().Be(88);
        result.Overview.EndDate.Should().Be(new DateOnly(2026, 10, 11));
        result.Overview.TotalEstimatedCost.Should().Be(75_000);
        result.Overview.ItemCount.Should().Be(2);
        result.Overview.CoverUrl.Should().Be("https://cdn.test/first.jpg");
        result.Itinerary.Should().HaveCount(2);
        result.Itinerary[0].Items.Select(item => item.Id).Should().Equal(501, 502);
        result.Itinerary[0].Items[0].Dish.Status.Should().Be("removed");
        result.Itinerary[0].Items[0].MyFeedback.Should().Be("dislike");
        result.Budget.Remaining.Should().Be(25_000);
        result.Budget.ByDay.Select(item => item.Cost).Should().Equal(75_000, 0);
        repository.Verify(item => item.GetFeedbackByItemIdsAsync(
            12,
            It.Is<IReadOnlyCollection<long>>(ids => ids.Order().SequenceEqual(new long[] { 501, 502 })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Return all empty days and avoid any fabricated cost or feedback.
    /// <summary>Verifies the lower boundary of an owned roadmap without itinerary items.</summary>
    [Fact]
    public async Task ExecuteAsync_WithEmptyRoadmap_ShouldReturnZeroCostAndAllDays()
    {
        var roadmap = CreateRoadmap();
        var repository = RepositoryReturning(roadmap, new Dictionary<long, FeedbackValue>());
        var useCase = CreateUseCase(repository.Object, userId: 12);

        var result = await useCase.ExecuteAsync(88, CancellationToken.None);

        result.Itinerary.Should().HaveCount(2);
        result.Itinerary.Should().OnlyContain(day => day.Items.Count == 0 && day.DayCost == 0);
        result.Budget.TotalEstimatedCost.Should().Be(0);
        result.Budget.Remaining.Should().Be(100_000);
        repository.Verify(item => item.GetFeedbackByItemIdsAsync(
            12,
            It.Is<IReadOnlyCollection<long>>(ids => ids.Count == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Hide a missing, deleted, or foreign roadmap behind ROADMAP_NOT_FOUND.
    /// <summary>Verifies RM-03 does not disclose ownership or soft-delete state.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenOwnedRoadmapDoesNotExist_ShouldThrowRoadmapNotFound()
    {
        var repository = RepositoryReturning(null, new Dictionary<long, FeedbackValue>());
        var useCase = CreateUseCase(repository.Object, userId: 12);

        var act = () => useCase.ExecuteAsync(404, CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("ROADMAP_NOT_FOUND");
        repository.Verify(item => item.GetFeedbackByItemIdsAsync(
            It.IsAny<int>(), It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Reject execution without an authenticated user before querying persistence.
    /// <summary>Verifies the Application boundary does not assume controller authorization.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        var repository = new Mock<IRoadmapRepository>();
        var useCase = CreateUseCase(repository.Object, userId: null);

        var act = () => useCase.ExecuteAsync(88, CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
        repository.VerifyNoOtherCalls();
    }

    /// <summary>Creates an owned two-day roadmap aggregate for RM-03 scenarios.</summary>
    private static Domain.Entities.Roadmap CreateRoadmap()
    {
        var roadmap = new Domain.Entities.Roadmap
        {
            Id = 88,
            UserId = 12,
            Title = "Trip",
            StartDate = new DateOnly(2026, 10, 10),
            NumDays = 2,
            BudgetAmount = 100_000,
            OriginMode = RoadmapOriginMode.None
        };
        roadmap.Days.Add(new RoadmapDay { Id = 301, DayNumber = 1, Roadmap = roadmap });
        roadmap.Days.Add(new RoadmapDay { Id = 302, DayNumber = 2, Roadmap = roadmap });
        return roadmap;
    }

    /// <summary>Adds deliberately unordered items with current prices and status to the test aggregate.</summary>
    private static Domain.Entities.Roadmap CreateRoadmapWithItems()
    {
        var roadmap = CreateRoadmap();
        var day = roadmap.Days.First();
        day.Items.Add(CreateItem(day, 502, MealSlot.Dinner, 0, 30_000, DishStatus.Active, null));
        day.Items.Add(CreateItem(day, 501, MealSlot.Breakfast, 1, 45_000, DishStatus.Removed,
            "https://cdn.test/first.jpg"));
        return roadmap;
    }

    /// <summary>Creates one fully navigable roadmap item required by the shared detail mapper.</summary>
    private static RoadmapItem CreateItem(
        RoadmapDay day,
        long id,
        MealSlot mealSlot,
        short sortOrder,
        decimal price,
        DishStatus status,
        string? coverUrl)
    {
        var restaurant = new Domain.Entities.Restaurant { Id = 7, Name = "Restaurant", AddressLine = "Address" };
        var dish = new Dish
        {
            Id = (int)id,
            Name = $"Dish {id}",
            Price = price,
            Status = status,
            Restaurant = restaurant,
            CoverMedia = coverUrl is null ? null : new MediaFile { Url = coverUrl }
        };
        return new RoadmapItem
        {
            Id = id,
            RoadmapDay = day,
            RoadmapDayId = day.Id,
            Dish = dish,
            DishId = dish.Id,
            MealSlot = mealSlot,
            SortOrder = sortOrder,
            Source = RoadmapItemSource.Manual
        };
    }

    /// <summary>Creates a repository mock for owned aggregate and feedback results.</summary>
    private static Mock<IRoadmapRepository> RepositoryReturning(
        Domain.Entities.Roadmap? roadmap,
        IReadOnlyDictionary<long, FeedbackValue> feedback)
    {
        var repository = new Mock<IRoadmapRepository>();
        repository.Setup(item => item.GetOwnedDetailAsync(It.IsAny<long>(), 12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(roadmap);
        repository.Setup(item => item.GetFeedbackByItemIdsAsync(
                12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(feedback);
        return repository;
    }

    /// <summary>Creates RM-03 with a deterministic current-user identity.</summary>
    private static GetRoadmapDetailUseCase CreateUseCase(IRoadmapRepository repository, int? userId)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(userId);
        return new GetRoadmapDetailUseCase(repository, currentUser.Object);
    }
}
