using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Roadmap.Generate;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Roadmap.Generate;

public sealed class ProcessRoadmapGenerationJobUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 2, 20, 4, DateTimeKind.Utc);
    private static readonly Guid RequestId = Guid.Parse("6f0c2b1e-8d7a-4c1e-9b3f-2a5d7e9c1f00");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // TEST-01: Without an AI answer, fill the default meals of every day with distinct ranked dishes, finish and notify.
    /// <summary>Verifies the whole run of a full-scope job planned by the fallback.</summary>
    [Fact]
    public async Task ExecuteAsync_WithFullScopeAndNoAi_ShouldWriteFallbackItemsAndFinishJob()
    {
        var fixture = new Fixture(new GenerateRoadmapRequestDto { Scope = "full", Province = "Huế", ExcludeTagIds = [12], MaxPricePerMeal = 60000 });

        await fixture.UseCase.ExecuteAsync(77, CancellationToken.None);

        fixture.Created.Should().HaveCount(6);
        fixture.Created.Select(item => item.DishId).Should().OnlyHaveUniqueItems();
        fixture.Created.Should().OnlyContain(item => item.Source == RoadmapItemSource.Ai && item.RecoRequestId == RequestId && item.SortOrder == 0);
        fixture.Created.Where(item => item.RoadmapDayId == 301).Select(item => item.MealSlot)
            .Should().Equal(MealSlot.Breakfast, MealSlot.Lunch, MealSlot.Dinner);
        var filter = fixture.Filters.Should().ContainSingle().Subject;
        filter.Should().BeEquivalentTo(new { Province = "Huế", MaxPrice = 60000m, Area = (NearbyArea?)null });
        filter.ExcludeTagIds.Should().Equal(12);
        fixture.Recommendation.Verify(value => value.RecommendAsync(12, It.IsAny<IReadOnlyList<int>>(), 200, RecommendationContext.RoadmapAi, It.IsAny<CancellationToken>()));

        var job = fixture.Tracked;
        (job.Status, job.ModelName, job.PromptVersion, job.RecoRequestId, job.FinishedAt).Should()
            .Be((GenerationJobStatus.Succeeded, "fallback", "rm10-v1", (Guid?)RequestId, (DateTime?)Now));
        using var output = JsonDocument.Parse(job.RawOutput!);
        output.RootElement.GetProperty("createdItemIds").EnumerateArray().Select(id => id.GetInt64())
            .Should().Equal(fixture.Created.Select(item => item.Id));
        output.RootElement.GetProperty("days")[0].GetProperty("plannedBy").GetString().Should().Be("fallback");
        output.RootElement.GetProperty("days")[0].GetProperty("assignments")[0].GetProperty("mealSlot").GetString().Should().Be("breakfast");

        var notification = fixture.Notifications.Should().ContainSingle().Subject;
        notification.Should().BeEquivalentTo(new
        {
            RecipientId = 12,
            Type = "ROADMAP_GENERATED",
            Title = "Roadmap đã được tạo xong",
            Body = "AI đã thêm 6 món vào roadmap \"Huế 2 ngày\"."
        });
        using var data = JsonDocument.Parse(notification.Data!);
        (data.RootElement.GetProperty("roadmap_id").GetInt64(), data.RootElement.GetProperty("job_id").GetInt64()).Should().Be((88, 77));
        fixture.Transaction.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        fixture.Recommendation.Verify(value => value.RecordImpressionsAsync(
            12, RequestId, It.Is<IReadOnlyList<int>>(ids => ids.Count == 6), Now, It.IsAny<CancellationToken>()));
    }

    // TEST-02: Keep a usable AI schedule and record the model that planned it.
    [Fact]
    public async Task ExecuteAsync_WithUsableAiPlan_ShouldWriteItAndRecordModel()
    {
        var fixture = new Fixture(new GenerateRoadmapRequestDto { Scope = "day", DayId = 301, Province = "Huế", MealSlots = ["dinner", "lunch"], Note = "Ít cay" });
        fixture.Planner.Setup(value => value.PlanDayAsync(It.IsAny<RoadmapPlanRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoadmapPlanResult("gpt-5.6-luna", [new(MealSlot.Dinner, 3), new(MealSlot.Lunch, 4)]));

        await fixture.UseCase.ExecuteAsync(77, CancellationToken.None);

        fixture.Created.Select(item => (item.RoadmapDayId, item.MealSlot, item.DishId)).Should()
            .Equal((301L, MealSlot.Lunch, 4), (301L, MealSlot.Dinner, 3));
        fixture.Tracked.ModelName.Should().Be("gpt-5.6-luna");
        fixture.Planner.Verify(value => value.PlanDayAsync(
            It.Is<RoadmapPlanRequest>(request => request.DayNumber == 1 && request.Note == "Ít cay" &&
                request.MealSlots.SequenceEqual(new[] { MealSlot.Lunch, MealSlot.Dinner }) &&
                request.Candidates[0].DishId == 1 && request.Candidates[0].DistanceFromAnchorKm == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Without replaceExisting, fill only the empty meals and never suggest a dish already in the roadmap.
    [Fact]
    public async Task ExecuteAsync_WithoutReplace_ShouldFillOnlyEmptyMeals()
    {
        var fixture = new Fixture(new GenerateRoadmapRequestDto { Scope = "day", DayId = 301, Province = "Huế" }, existingLunchDishId: 2);

        await fixture.UseCase.ExecuteAsync(77, CancellationToken.None);

        fixture.Created.Select(item => item.MealSlot).Should().Equal(MealSlot.Breakfast, MealSlot.Dinner);
        fixture.Created.Select(item => item.DishId).Should().NotContain(2);
        fixture.Filters.Single().ExcludeDishIds.Should().Equal(2);
        fixture.Removed.Should().BeEmpty();
    }

    // TEST-04: With replaceExisting, replace the items of the targeted meals and allow their dishes again.
    [Fact]
    public async Task ExecuteAsync_WithReplace_ShouldRemoveTargetedItems()
    {
        var fixture = new Fixture(
            new GenerateRoadmapRequestDto { Scope = "slot", DayId = 301, MealSlot = "lunch", ReplaceExisting = true, Province = "Huế" },
            existingLunchDishId: 2);

        await fixture.UseCase.ExecuteAsync(77, CancellationToken.None);

        fixture.Removed.Select(item => item.Id).Should().Equal(900);
        fixture.Filters.Single().ExcludeDishIds.Should().BeEmpty();
        var item = fixture.Created.Should().ContainSingle().Subject;
        (item.MealSlot, item.SortOrder).Should().Be((MealSlot.Lunch, (short)0));
    }

    // TEST-05: A slot that is already filled and not replaced finishes the job without asking reco.
    [Fact]
    public async Task ExecuteAsync_WithFilledSlotAndNoReplace_ShouldSucceedWithoutItems()
    {
        var fixture = new Fixture(new GenerateRoadmapRequestDto { Scope = "slot", DayId = 301, MealSlot = "lunch", Province = "Huế" }, existingLunchDishId: 2);

        await fixture.UseCase.ExecuteAsync(77, CancellationToken.None);

        fixture.Created.Should().BeEmpty();
        fixture.Tracked.Status.Should().Be(GenerationJobStatus.Succeeded);
        fixture.Notifications.Single().Body.Should().Be("AI đã thêm 0 món vào roadmap \"Huế 2 ngày\".");
        fixture.Recommendation.VerifyNoOtherCalls();
    }

    // TEST-06: Search the whole province when nothing is inside the radius, and record that the radius was dropped.
    [Fact]
    public async Task ExecuteAsync_WithNothingInsideRadius_ShouldDropRadius()
    {
        var fixture = new Fixture(
            new GenerateRoadmapRequestDto { Scope = "day", DayId = 301, Province = "Huế" },
            origin: new RoadmapGenerationOrigin("gps", 10.7769m, 106.7009m),
            emptyInsideArea: true);

        await fixture.UseCase.ExecuteAsync(77, CancellationToken.None);

        fixture.Filters.Select(filter => filter.Area is null).Should().Equal(false, true);
        fixture.Created.Should().HaveCount(3);
        using var input = JsonDocument.Parse(fixture.Tracked.Input);
        input.RootElement.GetProperty("radiusKm").ValueKind.Should().Be(JsonValueKind.Null);
        input.RootElement.GetProperty("radiusDropped").GetBoolean().Should().BeTrue();
        input.RootElement.GetProperty("origin").GetProperty("mode").GetString().Should().Be("gps");
        input.RootElement.GetProperty("candidateDishIds").GetArrayLength().Should().Be(8);
    }

    // TEST-07: Fail the job with a readable message when no dish matches, the roadmap was deleted or the day is gone.
    [Theory]
    [InlineData("no-candidate", ProcessRoadmapGenerationJobUseCase.NoCandidateMessage)]
    [InlineData("roadmap-deleted", ProcessRoadmapGenerationJobUseCase.RoadmapGoneMessage)]
    [InlineData("day-gone", ProcessRoadmapGenerationJobUseCase.DayGoneMessage)]
    public async Task ExecuteAsync_WhenNothingCanBeGenerated_ShouldFailJob(string reason, string message)
    {
        var fixture = new Fixture(
            new GenerateRoadmapRequestDto { Scope = "day", DayId = reason == "day-gone" ? 999 : 301, Province = "Huế" },
            noCandidate: reason == "no-candidate",
            deleted: reason == "roadmap-deleted");

        await fixture.UseCase.ExecuteAsync(77, CancellationToken.None);

        fixture.Jobs.Verify(value => value.MarkFailedAsync(77, message, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Created.Should().BeEmpty();
        fixture.Notifications.Should().BeEmpty();
        fixture.UnitOfWork.Verify(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-08: Never leave the job running after an unexpected error, and let the worker log it.
    [Fact]
    public async Task ExecuteAsync_WhenRecoThrows_ShouldFailJobAndRethrow()
    {
        var fixture = new Fixture(new GenerateRoadmapRequestDto { Scope = "full", Province = "Huế" });
        fixture.Recommendation.Setup(value => value.RecommendAsync(
                It.IsAny<int?>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<int?>(), It.IsAny<RecommendationContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => fixture.UseCase.ExecuteAsync(77, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        fixture.Jobs.Verify(value => value.MarkFailedAsync(77, ProcessRoadmapGenerationJobUseCase.UnexpectedMessage, CancellationToken.None), Times.Once);
    }

    // TEST-09: An Agent or manual edit during provider planning invalidates the RM-10 source snapshot before any item writes.
    /// <summary>Verifies a delayed generation job cannot overwrite a newer confirmed roadmap revision.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenRoadmapChangedDuringPlanning_ShouldFailWithoutOverwritingItems()
    {
        var fixture = new Fixture(new GenerateRoadmapRequestDto { Scope = "full", Province = "Huế", ReplaceExisting = true });
        fixture.Roadmaps.Setup(value => value.LockAndGetTrackedAsync(88, 12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Roadmap { Id = 88, UserId = 12, Version = 2 });

        await fixture.UseCase.ExecuteAsync(77, CancellationToken.None);

        fixture.Jobs.Verify(value => value.MarkFailedAsync(77, ProcessRoadmapGenerationJobUseCase.RoadmapChangedMessage, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Created.Should().BeEmpty(); fixture.Removed.Should().BeEmpty(); fixture.Notifications.Should().BeEmpty();
        fixture.UnitOfWork.Verify(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        fixture.Recommendation.Verify(value => value.RecordImpressionsAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class Fixture
    {
        public Fixture(
            GenerateRoadmapRequestDto request,
            int? existingLunchDishId = null,
            RoadmapGenerationOrigin? origin = null,
            bool emptyInsideArea = false,
            bool noCandidate = false,
            bool deleted = false)
        {
            var input = new RoadmapGenerationInput { Request = request, Origin = origin, RadiusKm = origin is null ? null : 10 };
            var scope = request.Scope switch { "day" => GenerationScope.Day, "slot" => GenerationScope.Slot, _ => GenerationScope.Full };
            var snapshot = new RoadmapGenerationJob
            {
                Id = 77,
                RoadmapId = 88,
                RequestedBy = 12,
                Scope = scope,
                Status = GenerationJobStatus.Running,
                Input = JsonSerializer.Serialize(input, Json),
                Roadmap = Roadmap(existingLunchDishId, deleted ? Now : null)
            };
            Tracked = new RoadmapGenerationJob { Id = 77, Input = snapshot.Input, Status = GenerationJobStatus.Running };

            // Eight dishes about 100 m apart, so one cluster covers every meal.
            var dishes = Enumerable.Range(1, 8).Select(id => new Dish
            {
                Id = id,
                Name = $"Món {id}",
                Price = 30000,
                Restaurant = new Domain.Entities.Restaurant { Name = $"Quán {id}", Latitude = 16.46m + id * 0.001m, Longitude = 107.59m }
            }).ToDictionary(dish => dish.Id);

            Jobs.Setup(value => value.GetJobSnapshotAsync(77, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
            Jobs.Setup(value => value.GetTrackedJobAsync(77, It.IsAny<CancellationToken>())).ReturnsAsync(Tracked);
            Jobs.Setup(value => value.AddNotification(It.IsAny<Domain.Entities.Notification>())).Callback<Domain.Entities.Notification>(Notifications.Add);
            Roadmaps.Setup(value => value.LockAndGetTrackedAsync(88, 12, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Roadmap(existingLunchDishId, null));
            Roadmaps.Setup(value => value.AddItem(It.IsAny<RoadmapItem>())).Callback<RoadmapItem>(Created.Add);
            Roadmaps.Setup(value => value.RemoveItem(It.IsAny<RoadmapItem>())).Callback<RoadmapItem>(Removed.Add);
            Dishes.Setup(value => value.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
                .ReturnsAsync((DishCandidateFilter filter, int _, CancellationToken _) =>
                {
                    Filters.Add(filter);
                    return noCandidate || (emptyInsideArea && filter.Area is not null)
                        ? []
                        : dishes.Keys.Except(filter.ExcludeDishIds).ToArray();
                });
            Dishes.Setup(value => value.GetCardDishesAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<int> ids, CancellationToken _) => ids.Select(id => dishes[id]).ToArray());
            Recommendation.Setup(value => value.RecommendAsync(
                    12, It.IsAny<IReadOnlyList<int>>(), 200, RecommendationContext.RoadmapAi, It.IsAny<CancellationToken>()))
                .ReturnsAsync((int? _, IReadOnlyList<int> ids, int? _, RecommendationContext _, CancellationToken _) =>
                    new RecommendationResult(RequestId, ids, []));
            Planner.SetupGet(value => value.PromptVersion).Returns("rm10-v1");
            UnitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Transaction.Object);
            var nextId = 1510L;
            UnitOfWork.Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Created.Where(item => item.Id == 0).ToList().ForEach(item => item.Id = nextId++))
                .ReturnsAsync(1);

            UseCase = new ProcessRoadmapGenerationJobUseCase(
                Jobs.Object, Roadmaps.Object, Dishes.Object, Recommendation.Object, Planner.Object, UnitOfWork.Object, new FixedTimeProvider(Now));
        }

        public ProcessRoadmapGenerationJobUseCase UseCase { get; }
        public RoadmapGenerationJob Tracked { get; }
        public Mock<IRoadmapGenerationRepository> Jobs { get; } = new();
        public Mock<IRoadmapRepository> Roadmaps { get; } = new();
        public Mock<IDishRepository> Dishes { get; } = new();
        public Mock<IRecommendationService> Recommendation { get; } = new();
        public Mock<IRoadmapPlannerService> Planner { get; } = new();
        public Mock<IUnitOfWork> UnitOfWork { get; } = new();
        public Mock<IUnitOfWorkTransaction> Transaction { get; } = new();
        public List<DishCandidateFilter> Filters { get; } = [];
        public List<RoadmapItem> Created { get; } = [];
        public List<RoadmapItem> Removed { get; } = [];
        public List<Domain.Entities.Notification> Notifications { get; } = [];

        private static Domain.Entities.Roadmap Roadmap(int? existingLunchDishId, DateTime? deletedAt)
        {
            var first = new RoadmapDay { Id = 301, RoadmapId = 88, DayNumber = 1 };
            if (existingLunchDishId is { } dishId)
            {
                first.Items.Add(new RoadmapItem { Id = 900, RoadmapDayId = 301, DishId = dishId, MealSlot = MealSlot.Lunch, Source = RoadmapItemSource.Manual });
            }

            return new Domain.Entities.Roadmap
            {
                Id = 88,
                UserId = 12,
                Title = "Huế 2 ngày",
                DeletedAt = deletedAt,
                Days = { first, new RoadmapDay { Id = 302, RoadmapId = 88, DayNumber = 2 } }
            };
        }
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
