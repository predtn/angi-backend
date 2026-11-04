using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.Services.Agent;
using ANGI.Application.Services.Agent.Conversation;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies tool allowlisting, trusted identity, filtering, reco-result validation and ordering dishes around a named place.</summary>
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

    // TEST-05: Reject unknown tools, injected user identity and a scope outside the schema enum before ranking; covers all three.
    /// <summary>Checks the backend allowlist independently of provider strict-mode promises.</summary>
    [Theory]
    [InlineData("unknown")][InlineData("identity")][InlineData("scope")]
    public async Task ExecuteAsync_InvalidTool_RejectsBeforeRanking(string area)
    {
        var fixture = AgentTestData.Validator(); var reco = new Mock<IRecommendationService>();
        var context = AgentTestData.Context();
        var call = area == "unknown" ? new AgentToolCall("c", "delete_roadmap", JsonSerializer.SerializeToElement(new { })) : Call(area == "scope" ? Input() with { Scope = "unknown" } : Input());
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

    // TEST-09: Routing arbitrary dish IDs and reading a roadmap not bound to the conversation return invalid_arguments to the model without
    // any route call or roadmap data, instead of failing the chat turn; covers both tools.
    /// <summary>Keeps ownership and candidate-source restrictions while letting the model recover from a bad read-only tool call.</summary>
    [Fact]
    public async Task ExecuteAsync_RouteAndRoadmap_RequireTrustedSources()
    {
        var fixture = AgentTestData.Validator();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        var route = new AgentToolCall("r", "estimate_day_route", JsonSerializer.SerializeToElement(new AgentRouteInput(1, [new("x", 999)]), AgentJson.Options));
        var roadmap = new AgentToolCall("m", "get_roadmap", JsonSerializer.SerializeToElement(new AgentRoadmapInput(99), AgentJson.Options));

        var routeResult = await tools.ExecuteAsync(AgentTestData.Context(), route, CancellationToken.None);
        var roadmapResult = await tools.ExecuteAsync(AgentTestData.Context(), roadmap, CancellationToken.None);

        routeResult.GetProperty("status").GetString().Should().Be("invalid_arguments");
        roadmapResult.GetProperty("status").GetString().Should().Be("invalid_arguments");
        roadmapResult.TryGetProperty("days", out _).Should().BeFalse();
        fixture.Routes.VerifyNoOtherCalls();
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
    // covers the reserve depth of 30 above the quota (2 days: 4 per meal x 3 = 12), a MaxRanked cap of 10 and slot scope.
    /// <summary>Checks the requested ranking size so de-duplication has headroom without exceeding the Recommendation contract.</summary>
    [Theory]
    [InlineData("full", 200, 30)][InlineData("full", 10, 10)][InlineData("slot", 200, 50)]
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
        context.Request.Content = "cả chuyến chỉ ăn Phở"; context.State = context.State with { MealSlots = ["lunch"], MealRequests = [new(null, null, "Phở", null, [])] };
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

    // TEST-15: A recommendation requested before the budget, duration or province is known returns missing_trip_details, flags the turn,
    // keeps the taste the model just accepted and never queries the catalog or reco; covers budget, days and province.
    /// <summary>Ensures missing trip values become a user question instead of a contract failure that turns the chat into HTTP 503.</summary>
    [Theory]
    [InlineData("budget")][InlineData("days")][InlineData("province")]
    public async Task ExecuteAsync_TripDetailsMissing_ReturnsNoticeWithoutRanking(string missing)
    {
        var fixture = AgentTestData.Validator(); var reco = new Mock<IRecommendationService>();
        var context = AgentTestData.Context();
        context.State = missing switch
        {
            "budget" => context.State with { BudgetAmount = null },
            "days" => context.State with { NumDays = null },
            _ => context.State with { Province = null }
        };
        var input = Input() with { State = context.State with { TasteTargets = [new("sour", .5)] } };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        var result = await tools.ExecuteAsync(context, Call(input), CancellationToken.None);

        result.GetProperty("status").GetString().Should().Be("missing_trip_details");
        result.GetProperty("items").GetArrayLength().Should().Be(0);
        context.TripDetailsMissing.Should().BeTrue();
        context.State.TasteTargets.Should().Equal(new AgentTasteTarget("sour", .5));
        context.Grants.Should().BeEmpty();
        fixture.Dishes.Verify(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        reco.VerifyNoOtherCalls();
    }

    // TEST-16: A whole-day taste the model proposes for "bữa 2 ngày 1" is narrowed to lunch of day 1 before the meal is ranked.
    /// <summary>Checks the backend binds a meal-specific taste to that meal even when the model widens it to the whole day.</summary>
    [Fact]
    public async Task ExecuteAsync_MealSpecificTasteWidenedByModel_IsNarrowedBeforeRanking()
    {
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1 });
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["breakfast", "lunch", "dinner"],
            PendingPlanningRequest = new(["Tôi muốn ăn ở Hà Nội 2 ngày, tôi thích ăn chua", "Vừa, áp dụng cho bữa 2 ngày 1"]) };
        // Model không gửi được pendingPlanningRequest (ngoài schema tool); backend tự giữ bản đã lưu.
        var input = Input() with { State = context.State with { PendingPlanningRequest = null, TasteOverrides = [new(1, null, [new("sour", .5)])] } };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        await tools.ExecuteAsync(context, Call(input), CancellationToken.None);

        context.State.TasteOverrides.Select(x => (x.DayNumber, x.MealSlot)).Should().Equal(((short)1, "lunch"));
        context.TripDetailsMissing.Should().BeFalse();
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

    // TEST-18: A dish with the same name as a dish kept outside the edited slot is not offered again, even at another restaurant.
    /// <summary>Prevents "món khác" from returning the same dish name from a different restaurant.</summary>
    [Fact]
    public async Task ExecuteAsync_EditedSlot_ExcludesSameNameAtOtherRestaurant()
    {
        var lunchTag = new ANGI.Domain.Entities.Tag { Id = 2, Code = "lunch", Name = "Bữa trưa", TagType = ANGI.Domain.Enums.TagType.MealTime };
        var fixture = AgentTestData.Validator(Named(1, "Phở", lunchTag), Named(2, "Phở", lunchTag), Named(4, "Bún", lunchTag));
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1, 2, 4 });
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1) with { DishName = "Phở" }, AgentTestData.Item(3, "b", 2) with { DishName = "Cơm" }));
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        await tools.ExecuteAsync(context, Call(Input() with { Scope = "slot", DayNumber = 2, MealSlot = "lunch" }), CancellationToken.None);

        context.Grants.Single().DishIds.Should().Equal(4);
    }

    // TEST-19: Malformed recommendation arguments from the model come back as invalid_arguments with the reason, without ranking or failing
    // the turn; covers topK 0 and 201, missing day, missing meal, another province, radius, fractional price, a negative
    // excluded ID, an unknown tag and a day beyond the trip.
    /// <summary>Lets the model correct a bad tool call within the turn instead of turning the chat into HTTP 503 or 409.</summary>
    [Theory]
    [InlineData("top-zero")][InlineData("top-large")][InlineData("day-missing")][InlineData("slot-missing")]
    [InlineData("province")][InlineData("radius")][InlineData("price")][InlineData("excluded-id")][InlineData("unknown-tag")][InlineData("day-beyond-trip")]
    public async Task ExecuteAsync_InvalidArguments_ReturnsErrorToModelWithoutRanking(string area)
    {
        var fixture = AgentTestData.Validator(); var reco = new Mock<IRecommendationService>(); var telemetry = new Mock<IAgentTelemetry>();
        var context = AgentTestData.Context();
        var input = area switch
        {
            "top-zero" => Input() with { TopK = 0 }, "top-large" => Input() with { TopK = 201 },
            "day-missing" => Input() with { Scope = "day" },
            "slot-missing" => Input() with { Scope = "slot", DayNumber = 1 }, "province" => Input() with { Province = "Other" },
            "radius" => Input() with { RadiusKm = 51 }, "price" => Input() with { MaxPricePerMeal = 1.5m },
            "excluded-id" => Input() with { ExcludeDishIds = [-1] }, "unknown-tag" => Input() with { TagIds = [99] },
            _ => Input() with { Scope = "day", DayNumber = 9 }
        };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), telemetry.Object);

        var result = await tools.ExecuteAsync(context, Call(input), CancellationToken.None);

        result.GetProperty("status").GetString().Should().Be("invalid_arguments");
        result.GetProperty("warnings")[0].GetString().Should().NotBeNullOrWhiteSpace();
        if (area == "unknown-tag") result.GetProperty("warnings")[0].GetString().Should().Contain("Tag 99");
        context.ToolNotice.Should().BeNull(); context.Grants.Should().BeEmpty();
        reco.VerifyNoOtherCalls();
        telemetry.Verify(x => x.Record("tool_argument_error", 7, It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<object?>()), Times.Once);
    }

    // TEST-20: An exact search_catalog match is kept only as an answer fact, never as a notice that replaces a clarification, while an
    // unfound name becomes both the turn notice and the catalog notice; covers a found and an unfound restaurant.
    /// <summary>Stops "Mình tìm thấy đúng món/quán bạn hỏi trên ANGI." from being sent as the reply to an unrelated question.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_SearchCatalog_FoundIsAnswerOnlyAndUnfoundIsNotice(bool found)
    {
        var fixture = AgentTestData.Validator();
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.SearchAsync("restaurant", "Lẩu Bò Mai", "Da Nang", 21, It.IsAny<CancellationToken>())).ReturnsAsync(found
            ? [new(575, "Lẩu Bò Mai", 575, "Lẩu Bò Mai", "Da Nang", "2 Tống Duy Tân", true)] : []);
        catalog.Setup(x => x.SearchAsync("restaurant", "Lẩu Bò Mai", null, 21, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var context = AgentTestData.Context();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);
        var call = new AgentToolCall("s", "search_catalog", JsonSerializer.SerializeToElement(new AgentCatalogInput("restaurant", "Lẩu Bò Mai", "Da Nang"), AgentJson.Options));

        var result = await tools.ExecuteAsync(context, call, CancellationToken.None);

        result.GetProperty("status").GetString().Should().Be(found ? "found" : "not_found");
        if (found)
        {
            context.ToolNotice.Should().BeNull(); context.CatalogNotice.Should().BeNull();
            context.CatalogAnswer.Should().StartWith("Mình tìm thấy đúng món/quán bạn hỏi trên ANGI.");
        }
        else
        {
            context.ToolNotice.Should().StartWith("Chưa tìm thấy món/quán"); context.CatalogNotice.Should().Be(context.ToolNotice);
            context.CatalogAnswer.Should().BeNull();
        }
    }

    // TEST-21: A named dish without a meal is placed on one fitting meal before ranking, so other meals keep their own candidates.
    /// <summary>Fixes "phải có món X" filtering every meal to X and ending in "no candidates for breakfast".</summary>
    [Fact]
    public async Task ExecuteAsync_NamedDishWithoutMeal_IsPlacedOnOneMeal()
    {
        var fixture = AgentTestData.Validator(Named(1, "Phở"), Named(2, "Bún"));
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DishCandidateFilter filter, int _, CancellationToken _) => filter.DishIds.Count > 0 ? [1] : new[] { 2 });
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.SearchAsync("dish", "Phở", "Da Nang", 21, It.IsAny<CancellationToken>())).ReturnsAsync([new(1, "Phở", 10, "Quán A", "Da Nang", "1 A", true)]);
        var context = AgentTestData.Context();
        context.Request.Content = "nhất định phải có món Phở";
        context.State = context.State with { MealSlots = ["breakfast", "lunch"], MealRequests = [new(null, null, "Phở", null, [])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);

        await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        context.State.MealRequests.Should().Equal(new AgentMealRequest(1, "lunch", "Phở", null, []));
        context.PlacedRequests.Should().Equal(new AgentMealRequest(1, "lunch", "Phở", null, []));
    }

    // TEST-22: A restaurant serving only dinner moves off a meal the backend chose; a meal the user named stays and reports no candidates.
    /// <summary>Fixes a replacement restaurant getting stuck on the lunch position its predecessor was placed on.</summary>
    [Theory]
    [InlineData("đổi sang quán Lẩu Bò", "dinner")]
    [InlineData("bữa trưa ngày 1 ăn ở quán Lẩu Bò", "lunch")]
    public async Task ExecuteAsync_RestaurantCannotServeMeal_MovesOnlyBackendPlacement(string content, string expected)
    {
        var fixture = AgentTestData.Validator(Named(1, "Lẩu"));
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DishCandidateFilter filter, int _, CancellationToken _) => filter.CompatibleMealTimeCode == "dinner" ? [1] : Array.Empty<int>());
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.SearchAsync("restaurant", "Lẩu Bò", "Da Nang", 21, It.IsAny<CancellationToken>())).ReturnsAsync([new(10, "Lẩu Bò", 10, "Lẩu Bò", "Da Nang", "1 A", true)]);
        var context = AgentTestData.Context();
        context.Request.Content = content;
        context.State = context.State with { MealSlots = ["lunch", "dinner"], MealRequests = [new(1, "lunch", null, "Lẩu Bò", [])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);

        await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        context.State.MealRequests.Single().MealSlot.Should().Be(expected);
    }

    // TEST-23: An unresolved name keeps its wide request so the ranking step reports it as a catalog question.
    /// <summary>Ensures placement never hides a dish the catalog does not have.</summary>
    [Fact]
    public async Task ExecuteAsync_UnknownNamedDish_StaysUnplacedAndAsks()
    {
        var fixture = AgentTestData.Validator();
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), 21, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var context = AgentTestData.Context();
        context.Request.Content = "phải có món Phở Sao Hỏa";
        context.State = context.State with { MealSlots = ["lunch"], MealRequests = [new(null, null, "Phở Sao Hỏa", null, [])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);

        await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        context.PlacedRequests.Should().BeEmpty();
        context.ToolNotice.Should().Contain("Phở Sao Hỏa");
    }

    // TEST-24: Pointing words are refused as a catalog name before any lookup and never become a "not found" notice.
    /// <summary>Stops "cái này" from producing a false claim that the dish is not in the catalog.</summary>
    [Fact]
    public async Task ExecuteAsync_SearchPointingWords_ReturnsArgumentErrorWithoutLookup()
    {
        var fixture = AgentTestData.Validator();
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        var context = AgentTestData.Context();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);
        var call = new AgentToolCall("s", "search_catalog", JsonSerializer.SerializeToElement(new AgentCatalogInput("dish", "cái này"), AgentJson.Options));

        var result = await tools.ExecuteAsync(context, call, CancellationToken.None);

        result.GetProperty("status").GetString().Should().Be("invalid_arguments");
        context.ToolNotice.Should().BeNull();
        catalog.VerifyNoOtherCalls();
    }

    // TEST-25: Dishes the user replaced earlier are not ranked again; when they are the only candidates they are still offered.
    /// <summary>Prevents repeated "món khác" from cycling back while never leaving a meal without candidates.</summary>
    [Theory]
    [InlineData(new[] { "Bún" }, new[] { 1 })]
    [InlineData(new[] { "Phở", "Bún" }, new[] { 1, 2 })]
    public async Task ExecuteAsync_ReplacedDishes_ExcludedUnlessOnlyChoice(string[] replaced, int[] expected)
    {
        var fixture = AgentTestData.Validator(Named(1, "Phở"), Named(2, "Bún"));
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1, 2 });
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["lunch"], ReplacedDishNames = replaced };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("dishId").GetInt32()).Should().Equal(expected);
    }

    // TEST-26: One full search splits days with different places, each searched around its own place, while a day without one searches the whole province and keeps its own grant.
    /// <summary>Ensures the model needs one call for the whole itinerary and that candidates of one day never authorize another.</summary>
    [Fact]
    public async Task ExecuteAsync_ItineraryDays_SearchEachDayAroundItsPlace()
    {
        var fixture = AgentTestData.Validator(Named(1, "Phở"), Named(2, "Bún"), Named(3, "Cơm"));
        var filters = new List<DishCandidateFilter>();
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback((DishCandidateFilter filter, int _, CancellationToken _) => filters.Add(filter))
            .ReturnsAsync((DishCandidateFilter filter, int _, CancellationToken _) => filter.Area?.Latitude == 16.05m ? new[] { 1 } : filter.Area?.Latitude == 16.06m ? new[] { 2 } : new[] { 3 });
        var context = AgentTestData.Context();
        context.State = context.State with { NumDays = 3, MealSlots = ["lunch"], DayAreas = [new(1, "A", 16.05m, 108.2m, 3m), new(2, "B", 16.06m, 108.2m, 5m)] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        filters.Select(filter => filter.Area?.RadiusKm).Should().Equal(3m, 5m, null);
        context.Grants.Select(grant => (grant.DayNumber, grant.DishIds.Single())).Should().Equal(((short?)1, 1), ((short?)2, 2), ((short?)3, 3));
        result.GetProperty("scopedResults").GetArrayLength().Should().Be(3);
    }

    // TEST-27: A place the itinerary holds is refused as a catalog name before any lookup, so the reply never claims it is "not found"; other names are still searched.
    /// <summary>Stops the failure of the reported conversation, where "Hoàn Kiếm" was searched as a restaurant name.</summary>
    [Fact]
    public async Task ExecuteAsync_SearchItineraryPlace_ReturnsArgumentErrorWithoutLookup()
    {
        var fixture = AgentTestData.Validator();
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        var context = AgentTestData.Context();
        context.State = context.State with { DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 3m)] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);
        var call = new AgentToolCall("s", "search_catalog", JsonSerializer.SerializeToElement(new AgentCatalogInput("restaurant", "hoàn kiếm"), AgentJson.Options));

        var result = await tools.ExecuteAsync(context, call, CancellationToken.None);

        result.GetProperty("status").GetString().Should().Be("invalid_arguments");
        context.ToolNotice.Should().BeNull();
        catalog.VerifyNoOtherCalls();
    }

    // TEST-28: A named place offers the nearer of two dishes with nearly equal scores first, asks reco for the deep list, and keeps a clearly better taste on top; covers a day of the itinerary and a whole-trip area.
    /// <summary>Ensures the near-first rule reaches the model's list and the grant order the backend repairs from.</summary>
    [Theory]
    [InlineData("day")]
    [InlineData("trip")]
    public async Task ExecuteAsync_NamedPlace_PrefersNearerAmongNearlyEqual(string area)
    {
        var (tools, reco, context) = NearbyScenario();
        context.State = context.State with { MealSlots = ["lunch"], NumDays = 1 };
        context.State = area == "day"
            ? context.State with { DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 5m)] }
            : context.State with { OriginMode = "pinned", OriginLatitude = 16.05m, OriginLongitude = 108.2m, OriginLabel = "Hoàn Kiếm", RadiusKm = 5m, NamedAreaLabel = "Hoàn Kiếm" };

        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("dishId").GetInt32()).Should().Equal(2, 1, 3);
        context.Grants.Single().DishIds.Should().Equal(2, 1, 3);
        reco.Verify(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), new AgentLimits().MaxRanked, It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-29: Every case already handled keeps reco's order and depth; covers the whole province, GPS, a pinned map origin and a label that no longer matches the origin.
    /// <summary>Guards the user's rule that this new case must not change what the system already does.</summary>
    [Theory]
    [InlineData("province")]
    [InlineData("gps")]
    [InlineData("pinned")]
    [InlineData("stale-label")]
    public async Task ExecuteAsync_NoNamedPlace_KeepsRecoOrder(string area)
    {
        var (tools, reco, context) = NearbyScenario();
        context.State = context.State with { MealSlots = ["lunch"], NumDays = 1 };
        context.State = area switch
        {
            "gps" => context.State with { OriginMode = "gps", OriginLatitude = 16.05m, OriginLongitude = 108.2m, OriginLabel = "Vị trí hiện tại", RadiusKm = 5m },
            "pinned" => context.State with { OriginMode = "pinned", OriginLatitude = 16.05m, OriginLongitude = 108.2m, OriginLabel = "Nhà", RadiusKm = 5m },
            "stale-label" => context.State with { OriginMode = "gps", OriginLatitude = 16.05m, OriginLongitude = 108.2m, OriginLabel = "Vị trí hiện tại", RadiusKm = 5m, NamedAreaLabel = "Hoàn Kiếm" },
            _ => context.State
        };

        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("dishId").GetInt32()).Should().Equal(1, 2, 3);
        reco.Verify(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), 30, It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-30: A rating fallback has no taste score to compare, so a named place keeps its order.
    /// <summary>Ensures distance never replaces the rating order when reco did not rank the dishes.</summary>
    [Fact]
    public async Task ExecuteAsync_NamedPlaceWithRatingFallback_KeepsOrder()
    {
        var (tools, _, context) = NearbyScenario();
        context.State = context.State with { MealSlots = ["lunch"], NumDays = 1, Preferences = null, DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 5m)] };

        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("dishId").GetInt32()).Should().Equal(1, 2, 3);
    }

    // TEST-31: A named place sends reco the nearest branch of a dish; without one the first-rated branch stays, as before.
    /// <summary>Ensures the same dish is offered close to the user only in the new case.</summary>
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task ExecuteAsync_SameDishBranches_NearestOnlyForNamedPlace(bool named, int expected)
    {
        var far = Named(1, "Phở"); far.Restaurant.Latitude = 16.08m;
        var near = Named(2, "Phở"); near.Restaurant.Latitude = 16.051m;
        var fixture = AgentTestData.Validator(far, near);
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1, 2 });
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["lunch"], NumDays = 1, DayAreas = named ? [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 5m)] : null };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        result.GetProperty("items").EnumerateArray().Single().GetProperty("dishId").GetInt32().Should().Be(expected);
    }

    // TEST-32: A meal with no dish in its named place searches further step by step, stops at the first step with a dish and records it; the day's other meals keep their radius.
    /// <summary>Implements the chosen rule: only the empty meal widens, with no question and no extra model call.</summary>
    [Fact]
    public async Task ExecuteAsync_MealWithoutDishInNamedPlace_WidensOnlyThatMeal()
    {
        var (tools, filters, context) = WideningScenario((filter) => filter.CompatibleMealTimeCode == "lunch" || filter.Area?.RadiusKm >= 8m ? new[] { filter.CompatibleMealTimeCode == "lunch" ? 2 : 1 } : []);
        context.State = context.State with { NumDays = 1, MealSlots = ["breakfast", "lunch"], DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 3m)] };

        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        filters.Where(filter => filter.CompatibleMealTimeCode == "breakfast").Select(filter => filter.Area!.RadiusKm).Should().Equal(3m, 5m, 8m);
        filters.Where(filter => filter.CompatibleMealTimeCode == "lunch").Select(filter => filter.Area!.RadiusKm).Should().Equal(3m);
        context.State.WidenedMeals.Should().Equal(new AgentWidenedMeal(1, "breakfast", 8m));
        context.Grants.Single(grant => grant.MealSlot == "breakfast").Constraints.WidenedMeals.Should().Equal(new AgentWidenedMeal(1, "breakfast", 8m));
        result.GetProperty("mealCoverage").EnumerateArray().Should().OnlyContain(meal => meal.GetProperty("availableCount").GetInt32() > 0);
    }

    // TEST-33: The same search keeps the old behaviour everywhere else; covers the whole province, GPS and a map origin.
    /// <summary>Guards the user's rule that the new case must not change meals already handled: they report a gap instead of widening.</summary>
    [Theory]
    [InlineData("province")]
    [InlineData("gps")]
    [InlineData("pinned")]
    public async Task ExecuteAsync_EmptyMealWithoutNamedPlace_DoesNotWiden(string area)
    {
        var (tools, filters, context) = WideningScenario(_ => []);
        context.State = context.State with { NumDays = 1, MealSlots = ["breakfast"] };
        context.State = area switch
        {
            "gps" => context.State with { OriginMode = "gps", OriginLatitude = 16.05m, OriginLongitude = 108.2m, OriginLabel = "Vị trí hiện tại", RadiusKm = 3m },
            "pinned" => context.State with { OriginMode = "pinned", OriginLatitude = 16.05m, OriginLongitude = 108.2m, OriginLabel = "Nhà", RadiusKm = 3m },
            _ => context.State
        };

        await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        filters.Should().ContainSingle();
        context.State.WidenedMeals.Should().BeNull();
    }

    // TEST-34: With nothing even at the largest radius the meal is reported as a real gap, and no wider radius is recorded.
    /// <summary>Ensures widening has an end, and that the existing coverage question still covers the last case.</summary>
    [Fact]
    public async Task ExecuteAsync_NoDishAtLargestRadius_ReportsGap()
    {
        var (tools, filters, context) = WideningScenario(_ => []);
        context.State = context.State with { NumDays = 1, MealSlots = ["breakfast"], DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 30m)] };

        await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        filters.Select(filter => filter.Area!.RadiusKm).Should().Equal(30m, 50m);
        context.State.WidenedMeals.Should().BeNull();
        context.MealCoverage.Single().AvailableCount.Should().Be(0);
    }

    // TEST-35: A later search that finds dishes inside the radius drops the recorded widening, and only for that meal.
    /// <summary>Ensures a stale wider radius cannot keep allowing far dishes once nearer ones exist.</summary>
    [Fact]
    public async Task ExecuteAsync_DishesBackInRadius_ClearsThatMealsWidening()
    {
        var (tools, _, context) = WideningScenario(_ => new[] { 1 });
        context.State = context.State with { NumDays = 1, MealSlots = ["lunch"], DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 3m)],
            WidenedMeals = [new(1, "lunch", 8m), new(1, "dinner", 12m)] };

        await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        context.State.WidenedMeals.Should().Equal(new AgentWidenedMeal(1, "dinner", 12m));
    }

    // TEST-36: A dish requested by name is not searched further out, because a wider circle cannot make a different dish the one the user asked for.
    /// <summary>Keeps the existing "thiếu món" answer for named dishes inside a named place.</summary>
    [Fact]
    public async Task ExecuteAsync_NamedDishMissingInPlace_DoesNotWiden()
    {
        var fixture = AgentTestData.Validator(Named(1, "Phở"));
        var filters = new List<DishCandidateFilter>();
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback((DishCandidateFilter filter, int _, CancellationToken _) => filters.Add(filter)).ReturnsAsync(Array.Empty<int>());
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.SearchAsync("dish", "Phở", It.IsAny<string?>(), 21, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AgentCatalogMatch(1, "Phở", 1, "Quán", "Da Nang", "Địa chỉ", true)]);
        var context = AgentTestData.Context();
        context.State = context.State with { NumDays = 1, MealSlots = ["breakfast", "lunch"], DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 3m)], MealRequests = [new(1, "lunch", "Phở", null, [])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);

        await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        filters.Where(filter => filter.Area is not null && filter.CompatibleMealTimeCode == "lunch").Select(filter => filter.Area!.RadiusKm).Should().Equal(3m);
        (context.State.WidenedMeals ?? []).Should().NotContain(meal => meal.MealSlot == "lunch");
    }

    /// <summary>Builds a search that answers from the given rule and records every filter it received.</summary>
    private static (AgentToolExecutor Tools, List<DishCandidateFilter> Filters, AgentContext Context) WideningScenario(Func<DishCandidateFilter, int[]> answer)
    {
        var fixture = AgentTestData.Validator(Named(1, "Phở"), Named(2, "Bún"));
        var filters = new List<DishCandidateFilter>();
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback((DishCandidateFilter filter, int _, CancellationToken _) => filters.Add(filter))
            .ReturnsAsync((DishCandidateFilter filter, int _, CancellationToken _) => answer(filter));
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        return (tools, filters, AgentTestData.Context());
    }

    /// <summary>Builds dishes 1 (far, best), 2 (near, nearly equal) and 3 (near, clearly worse) with a personalized reco result.</summary>
    private static (AgentToolExecutor Tools, Mock<IRecommendationService> Reco, AgentContext Context) NearbyScenario()
    {
        var far = Named(1, "Phở"); far.Restaurant.Latitude = 16.08m;
        var near = Named(2, "Bún"); near.Restaurant.Latitude = 16.051m;
        var worse = Named(3, "Cơm"); worse.Restaurant.Latitude = 16.05m;
        var fixture = AgentTestData.Validator(far, near, worse);
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1, 2, 3 });
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), It.IsAny<int>(), It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(Guid.NewGuid(), [1, 2, 3], [], [.9, .87, .6], "conversation"));
        var context = AgentTestData.Context();
        context.State = context.State with { Preferences = new() { Initialized = true } };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        return (tools, reco, context);
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

    private static readonly NearbyArea Center = new(21.03m, 105.85m, 5m);

    // TEST-37: Inside one band the nearer dish comes first; a dish clearly better on taste stays ahead however far it is.
    /// <summary>Ensures the rule the user chose: a near dish only overtakes a dish of about the same taste.</summary>
    [Fact]
    public void OrderNearby_NearlyEqualScores_NearerFirst_ClearlyBetterTasteStaysAhead()
    {
        var far = At(1, 0.04m);
        var near = At(2, 0.001m);
        var clearlyWorseButNext = At(3, 0m);

        var ordered = AgentToolExecutor.OrderNearby([far, near, clearlyWorseButNext], new Dictionary<int, double> { [1] = .90, [2] = .87, [3] = .70 }, Center);

        ordered.Select(dish => dish.Id).Should().Equal(2, 1, 3);
    }

    // TEST-38: The band edge: exactly under the limit shares a band with the best dish, at the limit starts the next band; covers the boundary both ways.
    /// <summary>Pins the meaning of "gần bằng nhau" so a later change to it is deliberate.</summary>
    [Theory]
    [InlineData(.9, new[] { 2, 1 })]
    [InlineData(.851, new[] { 2, 1 })]
    [InlineData(.84, new[] { 1, 2 })]
    public void OrderNearby_BandEdge_DecidesWhetherDistanceCounts(double second, int[] expected)
    {
        var ordered = AgentToolExecutor.OrderNearby([At(1, 0.04m), At(2, 0.001m)], new Dictionary<int, double> { [1] = .9, [2] = second }, Center);

        ordered.Select(dish => dish.Id).Should().Equal(expected);
    }

    // TEST-39: Equal scores, as for an account with no taste survey, are ordered by distance alone; equal distance keeps the ranking.
    /// <summary>Ensures long ties cannot hide a near restaurant, and the order stays stable when distance cannot decide.</summary>
    [Fact]
    public void OrderNearby_AllTied_UsesDistanceThenKeepsRanking()
    {
        var scores = new Dictionary<int, double> { [1] = .44, [2] = .44, [3] = .44 };

        AgentToolExecutor.OrderNearby([At(1, 0.03m), At(2, 0.01m), At(3, 0.02m)], scores, Center).Select(dish => dish.Id).Should().Equal(2, 3, 1);
        AgentToolExecutor.OrderNearby([At(1, 0.01m), At(2, 0.01m)], new Dictionary<int, double> { [1] = .44, [2] = .44 }, Center).Select(dish => dish.Id).Should().Equal(1, 2);
    }

    // TEST-40: Dishes with no score stay after scored ones in their order; a list with none is returned unchanged; an empty list stays empty.
    /// <summary>Ensures missing data never invents a position.</summary>
    [Fact]
    public void OrderNearby_MissingScores_StayLastInOrder()
    {
        var scores = new Dictionary<int, double> { [3] = .5 };

        AgentToolExecutor.OrderNearby([At(1, 0m), At(2, 0m), At(3, 0.04m)], scores, Center).Select(dish => dish.Id).Should().Equal(3, 1, 2);
        AgentToolExecutor.OrderNearby([At(1, 0.04m), At(2, 0m)], new Dictionary<int, double>(), Center).Select(dish => dish.Id).Should().Equal(1, 2);
        AgentToolExecutor.OrderNearby([], new Dictionary<int, double>(), Center).Should().BeEmpty();
    }

    // TEST-41: Branches of one dish reduce to the nearest while the dish keeps its first position; different dishes are untouched.
    /// <summary>Ensures "Phở" near the place is offered rather than the better-rated branch across the city.</summary>
    [Fact]
    public void NearestBranches_KeepsNearestAtFirstPosition()
    {
        var farBranch = At(1, 0.04m); farBranch.Name = "Phở";
        var other = At(2, 0.02m); other.Name = "Bún";
        var nearBranch = At(3, 0.001m); nearBranch.Name = "Phở";

        AgentToolExecutor.NearestBranches([farBranch, other, nearBranch], dish => dish.Name, Center).Select(dish => dish.Id).Should().Equal(3, 2);
    }

    /// <summary>Builds a dish whose restaurant lies the given degrees north of the centre.</summary>
    private static ANGI.Domain.Entities.Dish At(int id, decimal offset)
    {
        var dish = AgentTestData.Dish(id);
        dish.Restaurant.Latitude = Center.Latitude + offset;
        dish.Restaurant.Longitude = Center.Longitude;
        return dish;
    }
}
