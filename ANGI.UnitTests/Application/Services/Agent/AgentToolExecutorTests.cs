using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.Services.Agent;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies tool allowlisting, trusted identity, filtering and reco-result validation.</summary>
public sealed class AgentToolExecutorTests
{
    // TEST-01: Selection scope and allowed meal slots never widen a grant; covers full, day and slot scopes.
    /// <summary>Checks that issued grants and allowedMealSlots follow the requested day/meal scope and dish meal tags.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(1, null)]
    [InlineData(1, "breakfast")]
    public async Task ExecuteAsync_SelectionScopeAndMealCompatibility_DoNotWidenSlotGrants(int? dayNumber, string? mealSlot)
    {
        var dish = AgentTestData.Dish(221);
        var breakfastTag = new ANGI.Domain.Entities.Tag { Id = 1, Code = "breakfast", Name = "Bữa sáng",
            TagType = ANGI.Domain.Enums.TagType.MealTime };
        var lunchTag = new ANGI.Domain.Entities.Tag { Id = 2, Code = "lunch", Name = "Bữa trưa",
            TagType = ANGI.Domain.Enums.TagType.MealTime };
        dish.DishTags.Add(new() { TagId = 1, Tag = breakfastTag });
        dish.DishTags.Add(new() { TagId = 2, Tag = lunchTag });
        var fixture = AgentTestData.Validator(dish);
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { 221 });
        var context = AgentTestData.Context();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(),
            new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        var input = Input() with { Scope = dayNumber is null ? "full" : mealSlot is null ? "day" : "slot",
            DayNumber = (short?)dayNumber, MealSlot = mealSlot };

        var result = await tools.ExecuteAsync(context, Call(input), default);

        var scope = result.GetProperty("selectionScope");
        (scope.GetProperty("dayNumber").ValueKind == JsonValueKind.Null ? (int?)null : scope.GetProperty("dayNumber").GetInt32()).Should().Be(dayNumber);
        scope.GetProperty("mealSlot").GetString().Should().Be(mealSlot);
        result.GetProperty("items")[0].GetProperty("allowedMealSlots").EnumerateArray().Select(value => value.GetString())
            .Should().Equal(mealSlot is null ? new[] { "breakfast", "lunch" } : new[] { "breakfast" });
        context.Grants.Single().DayNumber.Should().Be((short?)dayNumber);
        context.Grants.Single().MealSlot.Should().Be(mealSlot);
    }

    // TEST-02: A day search ranks each declared meal on its own pool, reports a real empty meal and clears it after a slot search.
    /// <summary>A day search ranks each declared meal on its own pool, so a popular meal cannot hide another meal; real gaps stay visible.</summary>
    [Fact]
    public async Task ExecuteAsync_DayScopeWithDeclaredMeals_RanksEachMealSeparatelyAndReportsRealGap()
    {
        var lunchTag = new ANGI.Domain.Entities.Tag { Id = 2, Code = "lunch", Name = "Bữa trưa", TagType = ANGI.Domain.Enums.TagType.MealTime };
        var lunches = Enumerable.Range(1, 12).Select(id =>
        {
            var dish = AgentTestData.Dish(id);
            dish.DishTags.Add(new() { TagId = 2, Tag = lunchTag });
            return dish;
        }).ToArray();
        var breakfast = AgentTestData.Dish(13);
        var breakfastTag = new ANGI.Domain.Entities.Tag { Id = 1, Code = "breakfast", Name = "Bữa sáng", TagType = ANGI.Domain.Enums.TagType.MealTime };
        breakfast.DishTags.Add(new() { TagId = 1, Tag = breakfastTag });
        var fixture = AgentTestData.Validator(lunches.Append(breakfast).ToArray());
        var breakfastAvailable = false;
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DishCandidateFilter filter, int _, CancellationToken _) => filter.CompatibleMealTimeCode == "breakfast"
                ? breakfastAvailable ? new[] { 13 } : Array.Empty<int>() : lunches.Select(dish => dish.Id).ToArray());
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["breakfast", "lunch"] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(),
            new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        var dayResult = await tools.ExecuteAsync(context, Call(Input() with { Scope = "day", DayNumber = 1 }), CancellationToken.None);

        // Một ngày trong phạm vi: mỗi bữa giữ tối đa 1 + 2 món khác tên.
        dayResult.GetProperty("returnedCount").GetInt32().Should().Be(3);
        var coverage = dayResult.GetProperty("mealCoverage").EnumerateArray()
            .ToDictionary(meal => meal.GetProperty("mealSlot").GetString()!, meal => meal.GetProperty("availableCount").GetInt32());
        coverage.Should().Equal(new Dictionary<string, int> { ["breakfast"] = 0, ["lunch"] = 3 });
        dayResult.GetProperty("coverageWarning").GetString().Should().NotBeNullOrEmpty();
        AgentReplyBuilder.Coverage(context).Should().Contain("ngày 1, bữa sáng").And.NotContain("ngày 2");
        context.Grants.Single().MealSlot.Should().Be("lunch");

        breakfastAvailable = true;
        await tools.ExecuteAsync(context, Call(Input() with { Scope = "slot", DayNumber = 1, MealSlot = "breakfast" }), CancellationToken.None);

        AgentReplyBuilder.CoverageGaps(context).Should().BeEmpty();
        context.MealCoverage.Should().HaveCount(2);
    }

    // TEST-03: A full-trip search issues per-meal grants with distinct names, prefers names unused by earlier meals and returns a compact payload.
    /// <summary>A full-trip search returns per-meal grants with distinct names, prefers names unused by earlier meals and keeps the payload compact.</summary>
    [Fact]
    public async Task ExecuteAsync_FullTripWithDeclaredMeals_DeduplicatesNamesAndKeepsMealsDistinct()
    {
        var breakfastTag = new ANGI.Domain.Entities.Tag { Id = 1, Code = "breakfast", Name = "Bữa sáng", TagType = ANGI.Domain.Enums.TagType.MealTime };
        var lunchTag = new ANGI.Domain.Entities.Tag { Id = 2, Code = "lunch", Name = "Bữa trưa", TagType = ANGI.Domain.Enums.TagType.MealTime };
        var mainTag = new ANGI.Domain.Entities.Tag { Id = 5, Code = "local_sf_main", Name = "Món Chính", TagType = ANGI.Domain.Enums.TagType.Category };
        var catalog = new[]
        {
            Named(1, "Bánh mì", mainTag, breakfastTag), Named(2, "Bánh mì", mainTag, breakfastTag), Named(3, "Bánh mì", mainTag, breakfastTag),
            Named(4, "Phở", mainTag, breakfastTag, lunchTag), Named(5, "Xôi", mainTag, breakfastTag),
            Named(6, "Cơm", mainTag, lunchTag), Named(7, "Cơm", mainTag, lunchTag), Named(8, "Cơm", mainTag, lunchTag),
            Named(9, "Bún chả", mainTag, lunchTag), Named(11, "Bánh mì", mainTag, lunchTag)
        };
        var fixture = AgentTestData.Validator(catalog);
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DishCandidateFilter filter, int _, CancellationToken _) => filter.CompatibleMealTimeCode == "breakfast"
                ? new[] { 1, 2, 3, 4, 5 } : new[] { 4, 6, 7, 8, 9, 11 });
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["breakfast", "lunch"] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(),
            new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // Hai ngày: suất 4 món/bữa. Bữa sáng chỉ có 3 tên khác nhau; bữa trưa đưa tên chưa dùng (Cơm, Bún chả) lên trước.
        context.Grants.Select(grant => (grant.DayNumber, grant.MealSlot, string.Join(',', grant.DishIds)))
            .Should().Equal(((short?)null, "breakfast", "1,4,5"), ((short?)null, "lunch", "6,9,4,11"));
        var items = result.GetProperty("items").EnumerateArray().ToArray();
        items.Select(item => item.GetProperty("dishId").GetInt32()).Should().Equal(1, 4, 5, 6, 9, 11);
        items.Single(item => item.GetProperty("dishId").GetInt32() == 4).GetProperty("allowedMealSlots").EnumerateArray()
            .Select(slot => slot.GetString()).Should().Equal("breakfast", "lunch");
        items[0].TryGetProperty("latitude", out _).Should().BeFalse();
        items[0].GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).Should().Equal("local_sf_main");
        result.GetProperty("coverageWarning").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // TEST-04: Exclude previous scoped dishes before ranking alternatives and preserve fallback provenance.
    /// <summary>Verifies a deliberate alternative remains distinct from a transport retry or a recommender fallback.</summary>
    [Fact]
    public async Task ExecuteAsync_Alternative_FiltersBeforeRanking()
    {
        var fixture = AgentTestData.Validator(AgentTestData.Dish(2));
        DishCandidateFilter? captured = null;
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback<DishCandidateFilter, int, CancellationToken>((filter, _, _) => captured = filter).ReturnsAsync(new[] { 2 });
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.RecommendAsync(3, It.IsAny<IReadOnlyList<int>>(), 50, RecommendationContext.RoadmapAi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(null, [2], []));
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item())); context.Request.Command = "alternative";
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);
        captured!.ExcludeDishIds.Should().Contain(1); result.GetProperty("rankingMode").GetString().Should().Be("rating_fallback");
        context.Grants.Single().RequestId.Should().BeNull(); context.Grants.Single().DishIds.Should().Equal(2);
    }

    // TEST-05: Reject unknown tools, injected user identity and malformed numeric or scope arguments before ranking.
    /// <summary>Checks the backend allowlist independently of provider strict-mode promises.</summary>
    [Theory]
    [InlineData("unknown")][InlineData("identity")][InlineData("top-zero")][InlineData("top-large")][InlineData("scope")]
    [InlineData("day-missing")][InlineData("slot-missing")][InlineData("province")][InlineData("radius")][InlineData("price")]
    [InlineData("excluded-id")][InlineData("unknown-tag")][InlineData("budget")]
    public async Task ExecuteAsync_InvalidTool_RejectsBeforeRanking(string area)
    {
        var fixture = AgentTestData.Validator(); var reco = new Mock<IRecommendationService>();
        var context = AgentTestData.Context();
        var input = area switch
        {
            "top-zero" => Input() with { TopK = 0 }, "top-large" => Input() with { TopK = 201 },
            "scope" => Input() with { Scope = "unknown" }, "day-missing" => Input() with { Scope = "day" },
            "slot-missing" => Input() with { Scope = "slot", DayNumber = 1 }, "province" => Input() with { Province = "Other" },
            "radius" => Input() with { RadiusKm = 51 }, "price" => Input() with { MaxPricePerMeal = 1.5m },
            "excluded-id" => Input() with { ExcludeDishIds = [-1] }, "unknown-tag" => Input() with { TagIds = [99] }, _ => Input()
        };
        if (area == "budget") context.State = context.State with { BudgetAmount = null };
        var call = area == "unknown" ? new AgentToolCall("c", "delete_roadmap", JsonSerializer.SerializeToElement(new { })) : Call(input);
        if (area == "identity") call = call with { Arguments = JsonSerializer.SerializeToElement(new { userId = 99, roadmapId = 1 }) };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        await ((Func<Task>)(async () => await tools.ExecuteAsync(context, call, CancellationToken.None))).Should().ThrowAsync<AppException>();
        reco.VerifyNoOtherCalls();
    }

    // TEST-06: Reject out-of-candidate IDs, malformed scores and invalid request IDs in successful ranking responses.
    /// <summary>Validates the response contract before any ranked dish becomes authorized provenance.</summary>
    [Theory]
    [InlineData("foreign-id")][InlineData("foreign-ignored")][InlineData("missing-scores")][InlineData("score-count")]
    [InlineData("nonfinite-score")][InlineData("empty-request")][InlineData("fallback-scores")]
    public async Task ExecuteAsync_MalformedRanking_DoesNotIssueGrant(string area)
    {
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1 });
        var valid = new RecommendationResult(Guid.NewGuid(), [1], [], [0.5], "conversation");
        var result = area switch
        {
            "foreign-id" => valid with { DishIds = [999] }, "foreign-ignored" => valid with { IgnoredDishIds = [999] },
            "missing-scores" => valid with { Scores = null }, "score-count" => valid with { Scores = [] },
            "nonfinite-score" => valid with { Scores = [double.NaN] }, "empty-request" => valid with { RequestId = Guid.Empty },
            _ => valid with { RequestId = null }
        };
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), 50,
            It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        var context = AgentTestData.Context();
        context.State = context.State with { Preferences = new() { Initialized = true } };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        await ((Func<Task>)(async () => await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();
        context.Grants.Should().BeEmpty();
    }

    // TEST-07: An empty reco response for ignored candidates returns a notice and an empty grant instead of an error.
    /// <summary>Accepts an empty reco response when all catalog candidates are ignored.</summary>
    [Fact]
    public async Task ExecuteAsync_AllCandidatesIgnored_ReturnsNoResultsNoticeWithoutError()
    {
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000,
            It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1 });
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), 50,
            It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(null, [], [1], null, "conversation"));
        var context = AgentTestData.Context();
        context.State = context.State with { Preferences = new() { Initialized = true } };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object,
            new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        result.GetProperty("items").GetArrayLength().Should().Be(0);
        result.GetProperty("ignoredCount").GetInt32().Should().Be(1);
        context.ToolNotice.Should().NotBeNullOrWhiteSpace();
        context.Grants.Single().DishIds.Should().BeEmpty();
        context.Grants.Single().RequestId.Should().BeNull();
    }

    // TEST-08: Empty candidate sets ask for clarification without calling ranking or loosening constraints.
    /// <summary>Verifies the no-candidate business boundary.</summary>
    [Fact]
    public async Task ExecuteAsync_NoCandidates_ReturnsClarificationMetadata()
    {
        var fixture = AgentTestData.Validator(); var reco = new Mock<IRecommendationService>();
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<int>());
        var context = AgentTestData.Context();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);
        result.GetProperty("items").GetArrayLength().Should().Be(0); context.Grants.Should().BeEmpty(); reco.VerifyNoOtherCalls();
    }

    // TEST-09: Reject routing arbitrary dish IDs and reading a roadmap not bound to the conversation.
    /// <summary>Verifies ownership and candidate-source restrictions on the other two read-only tools.</summary>
    [Fact]
    public async Task ExecuteAsync_RouteAndRoadmap_RequireTrustedSources()
    {
        var fixture = AgentTestData.Validator();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        var route = new AgentToolCall("r", "estimate_day_route", JsonSerializer.SerializeToElement(new AgentRouteInput(1, [new("x", 999)]), AgentJson.Options));
        await ((Func<Task>)(async () => await tools.ExecuteAsync(AgentTestData.Context(), route, CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();
        var roadmap = new AgentToolCall("m", "get_roadmap", JsonSerializer.SerializeToElement(new AgentRoadmapInput(99), AgentJson.Options));
        await ((Func<Task>)(async () => await tools.ExecuteAsync(AgentTestData.Context(), roadmap, CancellationToken.None))).Should().ThrowAsync<NotFoundException>();
    }

    // TEST-10: When every declared meal has no candidate, the search returns the no-candidate notice, records zero coverage and never ranks.
    /// <summary>Verifies the per-meal search keeps the no-candidate boundary without calling Recommendation.</summary>
    [Fact]
    public async Task ExecuteAsync_PerMealSearchWithNoCandidates_ReturnsNoticeWithoutRanking()
    {
        var fixture = AgentTestData.Validator(); var reco = new Mock<IRecommendationService>();
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<int>());
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["breakfast", "lunch"] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        result.GetProperty("status").GetString().Should().Be("no_candidates");
        result.GetProperty("items").GetArrayLength().Should().Be(0);
        context.Grants.Should().BeEmpty(); context.ToolNotice.Should().Contain("ngày 1, bữa sáng");
        context.MealCoverage.Should().HaveCount(4).And.OnlyContain(meal => meal.AvailableCount == 0);
        reco.VerifyNoOtherCalls();
    }

    // TEST-11: A per-meal search over-requests ranking for name de-duplication up to MaxRanked while a slot search keeps the model's topK;
    // covers the uncapped quota (2 days: 4 per meal x 3 = 12), a MaxRanked cap of 10 and slot scope.
    /// <summary>Checks the requested ranking size so de-duplication has headroom without exceeding the Recommendation contract.</summary>
    [Theory]
    [InlineData("full", 200, 12)][InlineData("full", 10, 10)][InlineData("slot", 200, 50)]
    public async Task ExecuteAsync_RankingSize_DependsOnScopeAndMaxRanked(string scope, int maxRanked, int expected)
    {
        var fixture = AgentTestData.Validator(Enumerable.Range(1, 60).Select(id => AgentTestData.Dish(id)).ToArray());
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, 60).ToArray());
        var requested = new List<int>();
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), It.IsAny<int>(), It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, IReadOnlyList<int> ids, int count, RecommendationVector _, CancellationToken _) =>
            {
                requested.Add(count); var selected = ids.Take(count).ToArray();
                return new RecommendationResult(Guid.NewGuid(), selected, [], selected.Select(_ => .5).ToArray(), "conversation");
            });
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["lunch"], Preferences = new() { Initialized = true } };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object),
            new AgentLimits { MaxRanked = maxRanked }, Mock.Of<IAgentTelemetry>());
        var input = scope == "full" ? Input() with { TopK = 10 } : Input() with { Scope = "slot", DayNumber = 1, MealSlot = "lunch" };

        await tools.ExecuteAsync(context, Call(input), CancellationToken.None);

        requested.Should().Equal(expected);
    }

    // TEST-12: A malformed ranking in a per-meal search fails the tool and issues no grant.
    /// <summary>Keeps the reco response contract enforced for every meal-specific ranking call.</summary>
    [Fact]
    public async Task ExecuteAsync_PerMealSearchMalformedRanking_DoesNotIssueGrant()
    {
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1 });
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), It.IsAny<int>(), It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(Guid.NewGuid(), [999], [], [0.5], "conversation"));
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["breakfast", "lunch"], Preferences = new() { Initialized = true } };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        await ((Func<Task>)(async () => await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();
        context.Grants.Should().BeEmpty();
    }

    // TEST-13: A dish requested by name keeps every branch with that name so the model can ask which restaurant, instead of de-duplicating.
    /// <summary>Protects named-dish requests from losing branches through name de-duplication.</summary>
    [Fact]
    public async Task ExecuteAsync_NamedDishRequest_KeepsSameNameBranches()
    {
        var first = Named(1, "Phở"); first.RestaurantId = 10;
        var second = Named(2, "Phở"); second.RestaurantId = 11;
        var fixture = AgentTestData.Validator(first, second);
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1, 2 });
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.SearchAsync("dish", "Phở", "Da Nang", 21, It.IsAny<CancellationToken>())).ReturnsAsync(
            [new(1, "Phở", 10, "Quán A", "Da Nang", "1 A", true), new(2, "Phở", 11, "Quán B", "Da Nang", "2 B", true)]);
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["lunch"], MealRequests = [new(null, null, "Phở", null, [])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);

        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("dishId").GetInt32()).Should().Equal(1, 2);
        context.Grants.Single().DishIds.Should().Equal(1, 2);
    }

    // TEST-14: Caller cancellation during the per-meal candidate query propagates and issues no grant.
    /// <summary>Verifies the cancellation token reaches the catalog query and is not converted into a tool result.</summary>
    [Fact]
    public async Task ExecuteAsync_CallerCancellation_Propagates()
    {
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Returns((DishCandidateFilter _, int _, CancellationToken ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<int>>(new[] { 1 }); });
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["breakfast", "lunch"] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();

        await ((Func<Task>)(async () => await tools.ExecuteAsync(context, Call(Input()), cancellation.Token))).Should().ThrowAsync<OperationCanceledException>();
        context.Grants.Should().BeEmpty();
    }

    // TEST-17: A per-meal search grants the meal quota to the model and keeps the rest of the ranked distinct names as a reserve grant
    // for the same position, which is never returned to the model.
    /// <summary>Keeps unused ranked dishes available for backend repeat repair without enlarging the model's payload.</summary>
    [Fact]
    public async Task ExecuteAsync_PerMealSearch_KeepsRestOfRankingAsHiddenReserve()
    {
        var lunchTag = new ANGI.Domain.Entities.Tag { Id = 2, Code = "lunch", Name = "Bữa trưa", TagType = ANGI.Domain.Enums.TagType.MealTime };
        var mainTag = new ANGI.Domain.Entities.Tag { Id = 5, Code = "local_sf_main", Name = "Món Chính", TagType = ANGI.Domain.Enums.TagType.Category };
        var catalog = new[] { "Phở", "Bún chả", "Cơm tấm", "Bánh xèo", "Miến", "Bún riêu" }.Select((name, index) => Named(index + 1, name, mainTag, lunchTag)).ToArray();
        var fixture = AgentTestData.Validator(catalog);
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1, 2, 3, 4, 5, 6 });
        var context = AgentTestData.Context();
        context.State = context.State with { NumDays = 1, MealSlots = ["lunch"] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // Một ngày trong phạm vi: suất 1 + 2 = 3 món cho model, 3 món còn lại làm dự phòng.
        context.Grants.Single().DishIds.Should().Equal(1, 2, 3);
        var reserve = context.ReserveGrants.Single();
        reserve.DishIds.Should().Equal(4, 5, 6);
        (reserve.DayNumber, reserve.MealSlot).Should().Be(((short?)null, "lunch"));
        result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("dishId").GetInt32()).Should().Equal(1, 2, 3);
    }

    /// <summary>Builds a public dish with a fixed name and the given tags, for name de-duplication and meal-pool cases.</summary>
    private static ANGI.Domain.Entities.Dish Named(int id, string name, params ANGI.Domain.Entities.Tag[] tags)
    {
        var dish = AgentTestData.Dish(id); dish.Name = name;
        foreach (var tag in tags) dish.DishTags.Add(new() { TagId = tag.Id, Tag = tag });
        return dish;
    }

    /// <summary>Returns valid strict recommendation arguments with explicit empty lists.</summary>
    private static AgentRecommendationInput Input() => new(null, "Da Nang", "full", null, null, [], [], []);
    /// <summary>Serializes a complete strict tool call using the public camel-case contract.</summary>
    private static AgentToolCall Call(AgentRecommendationInput input) => new("c", "get_recommendation", JsonSerializer.SerializeToElement(input, AgentJson.Options));
}
