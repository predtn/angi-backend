using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.Services.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Places;
using ANGI.Domain.Entities;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies tool allowlisting, trusted identity, filtering, reco-result validation and ordering dishes around a named place.</summary>
public sealed class AgentToolExecutorTests
{
    // TEST-01: A generic phrase is not looked up as a dish, because "không tìm thấy món ăn bất kì" answers nothing the user asked.
    /// <summary>Sends the model to suggest_dish instead of the catalog.</summary>
    [Fact]
    public async Task ExecuteAsync_GenericName_AsksForSuggestion()
    {
        // STEP-01: Create fixture through AgentTestData.Validator, get context from AgentTestData.Context, create tools as AgentToolExecutor and 1 more.
        var fixture = AgentTestData.Validator();
        var context = AgentTestData.Context();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        var call = new AgentToolCall("s", "search_catalog", JsonSerializer.SerializeToElement(new AgentCatalogInput("dish", "món ăn bất kì"), AgentJson.Options));

        // STEP-02: Run tools.ExecuteAsync with context and call and keep result.
        var result = await tools.ExecuteAsync(context, call, CancellationToken.None);

        // STEP-03: Assert result.status is "invalid_arguments" and context.ToolNotice is null.
        result.GetProperty("status").GetString().Should().Be("invalid_arguments");
        context.ToolNotice.Should().BeNull();
    }

    // TEST-02: Selection scope and allowed meal slots never widen a grant; covers full, day and slot scopes.
    /// <summary>Checks that issued grants and allowedMealSlots follow the requested day/meal scope and dish meal tags.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(1, null)]
    [InlineData(1, "breakfast")]
    public async Task ExecuteAsync_SelectionScopeAndMealCompatibility_DoNotWidenSlotGrants(int? dayNumber, string? mealSlot)
    {
        // STEP-01: Get dish from AgentTestData.Dish, create breakfastTag as ANGI.Domain.Entities.Tag, create lunchTag as ANGI.Domain.Entities.Tag and 2 more.
        var dish = AgentTestData.Dish(221);
        var breakfastTag = new ANGI.Domain.Entities.Tag { Id = 1, Code = "breakfast", Name = "Bữa sáng",
            TagType = ANGI.Domain.Enums.TagType.MealTime };
        var lunchTag = new ANGI.Domain.Entities.Tag { Id = 2, Code = "lunch", Name = "Bữa trưa",
            TagType = ANGI.Domain.Enums.TagType.MealTime };
        dish.DishTags.Add(new() { TagId = 1, Tag = breakfastTag });
        dish.DishTags.Add(new() { TagId = 2, Tag = lunchTag });
        var fixture = AgentTestData.Validator(dish);
        // STEP-02: Stub fixture.GetCandidateIdsAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { 221 });
        // STEP-03: Get context from AgentTestData.Context, create tools as AgentToolExecutor and derive input from a copy of Input().
        var context = AgentTestData.Context();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(),
            new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        var input = Input() with { Scope = dayNumber is null ? "full" : mealSlot is null ? "day" : "slot",
            DayNumber = (short?)dayNumber, MealSlot = mealSlot };

        // STEP-04: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(input), default);

        // STEP-05: Get scope from result.GetProperty.
        var scope = result.GetProperty("selectionScope");
        // STEP-06: Assert the computed value is dayNumber, scope.mealSlot is mealSlot, EnumerateArray().Select(filtered) equals the expected value and 2 more.
        (scope.GetProperty("dayNumber").ValueKind == JsonValueKind.Null ? (int?)null : scope.GetProperty("dayNumber").GetInt32()).Should().Be(dayNumber);
        scope.GetProperty("mealSlot").GetString().Should().Be(mealSlot);
        result.GetProperty("items")[0].GetProperty("allowedMealSlots").EnumerateArray().Select(value => value.GetString())
            .Should().Equal(mealSlot is null ? new[] { "breakfast", "lunch" } : new[] { "breakfast" });
        context.Grants.Single().DayNumber.Should().Be((short?)dayNumber);
        context.Grants.Single().MealSlot.Should().Be(mealSlot);
    }

    // TEST-03: A day search ranks each declared meal on its own pool, reports a real empty meal and clears it after a slot search.
    /// <summary>A day search ranks each declared meal on its own pool, so a popular meal cannot hide another meal; real gaps stay visible.</summary>
    [Fact]
    public async Task ExecuteAsync_DayScopeWithDeclaredMeals_RanksEachMealSeparatelyAndReportsRealGap()
    {
        // STEP-01: Create lunchTag as ANGI.Domain.Entities.Tag, get lunches from Range().Select(filtered).ToArray, get breakfast from AgentTestData.Dish and 4 more.
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
        // STEP-02: Stub fixture.GetCandidateIdsAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DishCandidateFilter filter, int _, CancellationToken _) => filter.CompatibleMealTimeCode == "breakfast"
                ? breakfastAvailable ? new[] { 13 } : Array.Empty<int>() : lunches.Select(dish => dish.Id).ToArray());
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["breakfast", "lunch"] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(),
            new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Run tools.ExecuteAsync with context and keep dayResult.
        var dayResult = await tools.ExecuteAsync(context, Call(Input() with { Scope = "day", DayNumber = 1 }), CancellationToken.None);

        // STEP-05: Assert dayResult.returnedCount is 3.
        // Một ngày trong phạm vi: mỗi bữa giữ tối đa 1 + 2 món khác tên.
        dayResult.GetProperty("returnedCount").GetInt32().Should().Be(3);
        // STEP-06: Get coverage from mealCoverage.EnumerateArray().ToDictionary.
        var coverage = dayResult.GetProperty("mealCoverage").EnumerateArray()
            .ToDictionary(meal => meal.GetProperty("mealSlot").GetString()!, meal => meal.GetProperty("availableCount").GetInt32());
        // STEP-07: Assert coverage equals a new Dictionary<string, int>, dayResult.coverageWarning has a value and 2 more.
        coverage.Should().Equal(new Dictionary<string, int> { ["breakfast"] = 0, ["lunch"] = 3 });
        dayResult.GetProperty("coverageWarning").GetString().Should().NotBeNullOrEmpty();
        AgentReplyBuilder.Coverage(context).Should().Contain("ngày 1, bữa sáng").And.NotContain("ngày 2");
        context.Grants.Single().MealSlot.Should().Be("lunch");

        // STEP-08: Set breakfastAvailable to true.
        breakfastAvailable = true;
        // STEP-09: Run tools.ExecuteAsync with context.
        await tools.ExecuteAsync(context, Call(Input() with { Scope = "slot", DayNumber = 1, MealSlot = "breakfast" }), CancellationToken.None);

        // STEP-10: Assert AgentReplyBuilder.CoverageGaps() is empty and context.MealCoverage has 2 item(s).
        AgentReplyBuilder.CoverageGaps(context).Should().BeEmpty();
        context.MealCoverage.Should().HaveCount(2);
    }

    // TEST-04: A full-trip search issues per-meal grants with distinct names, prefers names unused by earlier meals and returns a compact payload.
    /// <summary>A full-trip search returns per-meal grants with distinct names, prefers names unused by earlier meals and keeps the payload compact.</summary>
    [Fact]
    public async Task ExecuteAsync_FullTripWithDeclaredMeals_DeduplicatesNamesAndKeepsMealsDistinct()
    {
        // STEP-01: Create breakfastTag as ANGI.Domain.Entities.Tag, create lunchTag as ANGI.Domain.Entities.Tag, create mainTag as ANGI.Domain.Entities.Tag and 2 more.
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
        // STEP-02: Stub fixture.GetCandidateIdsAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DishCandidateFilter filter, int _, CancellationToken _) => filter.CompatibleMealTimeCode == "breakfast"
                ? new[] { 1, 2, 3, 4, 5 } : new[] { 4, 6, 7, 8, 9, 11 });
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["breakfast", "lunch"] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(),
            new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-05: Assert context.Grants.Select(filtered) equals ((short?)null, "breakfast", "1,4,5").
        // Hai ngày: suất 4 món/bữa. Bữa sáng chỉ có 3 tên khác nhau; bữa trưa đưa tên chưa dùng (Cơm, Bún chả) lên trước.
        context.Grants.Select(grant => (grant.DayNumber, grant.MealSlot, string.Join(',', grant.DishIds)))
            .Should().Equal(((short?)null, "breakfast", "1,4,5"), ((short?)null, "lunch", "6,9,4,11"));
        // STEP-06: Get items from result.items.EnumerateArray().ToArray.
        var items = result.GetProperty("items").EnumerateArray().ToArray();
        // STEP-07: Assert items.Select(filtered) equals 1, EnumerateArray().Select(filtered) equals "breakfast", items[0].TryGetProperty() is false and 2 more.
        items.Select(item => item.GetProperty("dishId").GetInt32()).Should().Equal(1, 4, 5, 6, 9, 11);
        items.Single(item => item.GetProperty("dishId").GetInt32() == 4).GetProperty("allowedMealSlots").EnumerateArray()
            .Select(slot => slot.GetString()).Should().Equal("breakfast", "lunch");
        items[0].TryGetProperty("latitude", out _).Should().BeFalse();
        items[0].GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).Should().Equal("local_sf_main");
        result.GetProperty("coverageWarning").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // TEST-05: Exclude previous scoped dishes before ranking alternatives and preserve fallback provenance.
    /// <summary>Verifies a deliberate alternative remains distinct from a transport retry or a recommender fallback.</summary>
    [Fact]
    public async Task ExecuteAsync_Alternative_FiltersBeforeRanking()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator(AgentTestData.Dish(2));
        // STEP-02: Declare captured to capture a call argument; mock IRecommendationService; stub fixture.GetCandidateIdsAsync and reco.RecommendAsync.
        DishCandidateFilter? captured = null;
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback<DishCandidateFilter, int, CancellationToken>((filter, _, _) => captured = filter).ReturnsAsync(new[] { 2 });
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.RecommendAsync(3, It.IsAny<IReadOnlyList<int>>(), 50, RecommendationContext.RoadmapAi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(null, [2], []));
        // STEP-03: Get context from AgentTestData.Context, set context.Request.Command to "alternative" and create tools as AgentToolExecutor.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item())); context.Request.Command = "alternative";
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        // STEP-04: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);
        // STEP-05: Assert captured.ExcludeDishIds contains 1, result.rankingMode is "rating_fallback", context.Grants.Single().RequestId is null and 1 more.
        captured!.ExcludeDishIds.Should().Contain(1); result.GetProperty("rankingMode").GetString().Should().Be("rating_fallback");
        context.Grants.Single().RequestId.Should().BeNull(); context.Grants.Single().DishIds.Should().Equal(2);
    }

    // TEST-06: Reject unknown tools, injected user identity and a scope outside the schema enum before ranking; covers all three.
    /// <summary>Checks the backend allowlist independently of provider strict-mode promises.</summary>
    [Theory]
    [InlineData("unknown")][InlineData("identity")][InlineData("scope")]
    public async Task ExecuteAsync_InvalidTool_RejectsBeforeRanking(string area)
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        // STEP-02: Mock IRecommendationService.
        var fixture = AgentTestData.Validator(); var reco = new Mock<IRecommendationService>();
        // STEP-03: Get context from AgentTestData.Context, derive call from the expected value, when area == "identity", set call to a copy of call and 1 more.
        var context = AgentTestData.Context();
        var call = area == "unknown" ? new AgentToolCall("c", "delete_roadmap", JsonSerializer.SerializeToElement(new { })) : Call(area == "scope" ? Input() with { Scope = "unknown" } : Input());
        if (area == "identity") call = call with { Arguments = JsonSerializer.SerializeToElement(new { userId = 99, roadmapId = 1 }) };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        // STEP-04: Assert tools.ExecuteAsync() throws AppException.
        await ((Func<Task>)(async () => await tools.ExecuteAsync(context, call, CancellationToken.None))).Should().ThrowAsync<AppException>();
        // STEP-05: Verify reco receives no other calls.
        reco.VerifyNoOtherCalls();
    }

    // TEST-07: Reject out-of-candidate IDs, malformed scores and invalid request IDs in successful ranking responses.
    /// <summary>Validates the response contract before any ranked dish becomes authorized provenance.</summary>
    [Theory]
    [InlineData("foreign-id")][InlineData("foreign-ignored")][InlineData("missing-scores")][InlineData("score-count")]
    [InlineData("nonfinite-score")][InlineData("empty-request")][InlineData("fallback-scores")]
    public async Task ExecuteAsync_MalformedRanking_DoesNotIssueGrant(string area)
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        // STEP-02: Stub fixture.GetCandidateIdsAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1 });
        // STEP-03: Create valid as RecommendationResult and derive result from the expected value.
        var valid = new RecommendationResult(Guid.NewGuid(), [1], [], [0.5], "conversation");
        var result = area switch
        {
            "foreign-id" => valid with { DishIds = [999] }, "foreign-ignored" => valid with { IgnoredDishIds = [999] },
            "missing-scores" => valid with { Scores = null }, "score-count" => valid with { Scores = [] },
            "nonfinite-score" => valid with { Scores = [double.NaN] }, "empty-request" => valid with { RequestId = Guid.Empty },
            _ => valid with { RequestId = null }
        };
        // STEP-04: Mock IRecommendationService; stub reco.RecommendConversationAsync.
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), 50,
            It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        // STEP-05: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { Preferences = new() { Initialized = true } };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        // STEP-06: Assert tools.ExecuteAsync() throws ServiceUnavailableException and context.Grants is empty.
        await ((Func<Task>)(async () => await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();
        context.Grants.Should().BeEmpty();
    }

    // TEST-08: An empty reco response for ignored candidates returns a notice and an empty grant instead of an error.
    /// <summary>Accepts an empty reco response when all catalog candidates are ignored.</summary>
    [Fact]
    public async Task ExecuteAsync_AllCandidatesIgnored_ReturnsNoResultsNoticeWithoutError()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        // STEP-02: Mock IRecommendationService; stub fixture.GetCandidateIdsAsync and reco.RecommendConversationAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000,
            It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1 });
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), 50,
            It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(null, [], [1], null, "conversation"));
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { Preferences = new() { Initialized = true } };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object,
            new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-05: Assert result.items.length is 0, result.ignoredCount is 1, context.ToolNotice has a value, context.Grants.Single().DishIds is empty and 1 more.
        result.GetProperty("items").GetArrayLength().Should().Be(0);
        result.GetProperty("ignoredCount").GetInt32().Should().Be(1);
        context.ToolNotice.Should().NotBeNullOrWhiteSpace();
        context.Grants.Single().DishIds.Should().BeEmpty();
        context.Grants.Single().RequestId.Should().BeNull();
    }

    // TEST-09: Empty candidate sets ask for clarification without calling ranking or loosening constraints.
    /// <summary>Verifies the no-candidate business boundary.</summary>
    [Fact]
    public async Task ExecuteAsync_NoCandidates_ReturnsClarificationMetadata()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        // STEP-02: Mock IRecommendationService; stub fixture.GetCandidateIdsAsync.
        var fixture = AgentTestData.Validator(); var reco = new Mock<IRecommendationService>();
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<int>());
        // STEP-03: Get context from AgentTestData.Context and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        // STEP-04: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);
        // STEP-05: Assert result.items.length is 0 and context.Grants is empty.
        // STEP-06: Verify reco receives no other calls.
        result.GetProperty("items").GetArrayLength().Should().Be(0); context.Grants.Should().BeEmpty(); reco.VerifyNoOtherCalls();
    }

    // TEST-10: Routing arbitrary dish IDs and reading a roadmap not bound to the conversation return invalid_arguments to the model without
    // any route call or roadmap data, instead of failing the chat turn; covers both tools.
    /// <summary>Keeps ownership and candidate-source restrictions while letting the model recover from a bad read-only tool call.</summary>
    [Fact]
    public async Task ExecuteAsync_RouteAndRoadmap_RequireTrustedSources()
    {
        // STEP-01: Create fixture through AgentTestData.Validator, create tools as AgentToolExecutor, create route as AgentToolCall and create roadmap as AgentToolCall.
        var fixture = AgentTestData.Validator();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        var route = new AgentToolCall("r", "estimate_day_route", JsonSerializer.SerializeToElement(new AgentRouteInput(1, [new("x", 999)]), AgentJson.Options));
        var roadmap = new AgentToolCall("m", "get_roadmap", JsonSerializer.SerializeToElement(new AgentRoadmapInput(99), AgentJson.Options));

        // STEP-02: Run tools.ExecuteAsync with route and keep routeResult.
        var routeResult = await tools.ExecuteAsync(AgentTestData.Context(), route, CancellationToken.None);
        // STEP-03: Run tools.ExecuteAsync with roadmap and keep roadmapResult.
        var roadmapResult = await tools.ExecuteAsync(AgentTestData.Context(), roadmap, CancellationToken.None);

        // STEP-04: Assert routeResult.status is "invalid_arguments", roadmapResult.status is "invalid_arguments" and roadmapResult.TryGetProperty() is false.
        routeResult.GetProperty("status").GetString().Should().Be("invalid_arguments");
        roadmapResult.GetProperty("status").GetString().Should().Be("invalid_arguments");
        roadmapResult.TryGetProperty("days", out _).Should().BeFalse();
        // STEP-05: Verify fixture receives no other calls.
        fixture.Routes.VerifyNoOtherCalls();
    }

    // TEST-11: When every declared meal has no candidate, the search returns the no-candidate notice, records zero coverage and never ranks.
    /// <summary>Verifies the per-meal search keeps the no-candidate boundary without calling Recommendation.</summary>
    [Fact]
    public async Task ExecuteAsync_PerMealSearchWithNoCandidates_ReturnsNoticeWithoutRanking()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        // STEP-02: Mock IRecommendationService; stub fixture.GetCandidateIdsAsync.
        var fixture = AgentTestData.Validator(); var reco = new Mock<IRecommendationService>();
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<int>());
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["breakfast", "lunch"] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-05: Assert result.status is "no_candidates", result.items.length is 0, context.Grants is empty, context.ToolNotice contains the expected text and 1 more.
        result.GetProperty("status").GetString().Should().Be("no_candidates");
        result.GetProperty("items").GetArrayLength().Should().Be(0);
        context.Grants.Should().BeEmpty(); context.ToolNotice.Should().Contain("ngày 1, bữa sáng");
        context.MealCoverage.Should().HaveCount(4).And.OnlyContain(meal => meal.AvailableCount == 0);
        // STEP-06: Verify reco receives no other calls.
        reco.VerifyNoOtherCalls();
    }

    // TEST-12: A per-meal search over-requests ranking for name de-duplication up to MaxRanked while a slot search keeps the model's topK;
    // covers the reserve depth of 30 above the quota (2 days: 4 per meal x 3 = 12), a MaxRanked cap of 10 and slot scope.
    /// <summary>Checks the requested ranking size so de-duplication has headroom without exceeding the Recommendation contract.</summary>
    [Theory]
    [InlineData("full", 200, 30)][InlineData("full", 10, 10)][InlineData("slot", 200, 50)]
    public async Task ExecuteAsync_RankingSize_DependsOnScopeAndMaxRanked(string scope, int maxRanked, int expected)
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator(Enumerable.Range(1, 60).Select(id => AgentTestData.Dish(id)).ToArray());
        // STEP-02: Create requested as List<int>; mock IRecommendationService; stub fixture.GetCandidateIdsAsync and reco.RecommendConversationAsync.
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
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State, create tools as AgentToolExecutor and 1 more.
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["lunch"], Preferences = new() { Initialized = true } };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object),
            new AgentLimits { MaxRanked = maxRanked }, Mock.Of<IAgentTelemetry>());
        var input = scope == "full" ? Input() with { TopK = 10 } : Input() with { Scope = "slot", DayNumber = 1, MealSlot = "lunch" };

        // STEP-04: Run tools.ExecuteAsync with context.
        await tools.ExecuteAsync(context, Call(input), CancellationToken.None);

        // STEP-05: Assert requested equals expected.
        requested.Should().Equal(expected);
    }

    // TEST-13: A malformed ranking in a per-meal search fails the tool and issues no grant.
    /// <summary>Keeps the reco response contract enforced for every meal-specific ranking call.</summary>
    [Fact]
    public async Task ExecuteAsync_PerMealSearchMalformedRanking_DoesNotIssueGrant()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        // STEP-02: Mock IRecommendationService; stub fixture.GetCandidateIdsAsync and reco.RecommendConversationAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1 });
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), It.IsAny<int>(), It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(Guid.NewGuid(), [999], [], [0.5], "conversation"));
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["breakfast", "lunch"], Preferences = new() { Initialized = true } };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Assert tools.ExecuteAsync() throws ServiceUnavailableException and context.Grants is empty.
        await ((Func<Task>)(async () => await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();
        context.Grants.Should().BeEmpty();
    }

    // TEST-14: A dish requested by name keeps every branch with that name so the model can ask which restaurant, instead of de-duplicating.
    /// <summary>Protects named-dish requests from losing branches through name de-duplication.</summary>
    [Fact]
    public async Task ExecuteAsync_NamedDishRequest_KeepsSameNameBranches()
    {
        // STEP-01: Get first from Named, set first.RestaurantId to 10, get second from Named, set second.RestaurantId to 11 and 1 more.
        var first = Named(1, "Phở"); first.RestaurantId = 10;
        var second = Named(2, "Phở"); second.RestaurantId = 11;
        var fixture = AgentTestData.Validator(first, second);
        // STEP-02: Mock ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository; stub fixture.GetCandidateIdsAsync and catalog.SearchAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1, 2 });
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.SearchAsync("dish", "Phở", "Da Nang", 21, It.IsAny<CancellationToken>())).ReturnsAsync(
            [new(1, "Phở", 10, "Quán A", "Da Nang", "1 A", true), new(2, "Phở", 11, "Quán B", "Da Nang", "2 B", true)]);
        // STEP-03: Get context from AgentTestData.Context, set context.Request.Content to a test text, set context.State to a copy of context.State and 1 more.
        var context = AgentTestData.Context();
        context.Request.Content = "cả chuyến chỉ ăn Phở"; context.State = context.State with { MealSlots = ["lunch"], MealRequests = [new(null, null, "Phở", null, [])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);

        // STEP-04: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-05: Assert result.items.EnumerateArray().Select(filtered) equals 1 and context.Grants.Single().DishIds equals 1.
        result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("dishId").GetInt32()).Should().Equal(1, 2);
        context.Grants.Single().DishIds.Should().Equal(1, 2);
    }

    // TEST-15: Caller cancellation during the per-meal candidate query propagates and issues no grant.
    /// <summary>Verifies the cancellation token reaches the catalog query and is not converted into a tool result.</summary>
    [Fact]
    public async Task ExecuteAsync_CallerCancellation_Propagates()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        // STEP-02: Stub fixture.GetCandidateIdsAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Returns((DishCandidateFilter _, int _, CancellationToken ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<int>>(new[] { 1 }); });
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State, create tools as AgentToolExecutor and 2 more.
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["breakfast", "lunch"] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();

        // STEP-04: Assert tools.ExecuteAsync() throws OperationCanceledException and context.Grants is empty.
        await ((Func<Task>)(async () => await tools.ExecuteAsync(context, Call(Input()), cancellation.Token))).Should().ThrowAsync<OperationCanceledException>();
        context.Grants.Should().BeEmpty();
    }

    // TEST-16: A recommendation requested before the budget, duration or province is known returns missing_trip_details, flags the turn,
    // keeps the taste the model just accepted and never queries the catalog or reco; covers budget, days and province.
    /// <summary>Ensures missing trip values become a user question instead of a contract failure that turns the chat into HTTP 503.</summary>
    [Theory]
    [InlineData("days")][InlineData("province")]
    public async Task ExecuteAsync_TripDetailsMissing_ReturnsNoticeWithoutRanking(string missing)
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        // STEP-02: Mock IRecommendationService.
        var fixture = AgentTestData.Validator(); var reco = new Mock<IRecommendationService>();
        // STEP-03: Get context from AgentTestData.Context, set context.State to a test value, derive input from a copy of Input() and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = missing switch
        {
            "budget" => context.State with { BudgetAmount = null },
            "days" => context.State with { NumDays = null },
            _ => context.State with { Province = null }
        };
        context.Request.Content = "chua vừa";
        var input = Input() with { State = context.State with { TasteTargets = [new("sour", .5, "chua vừa")] } };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(input), CancellationToken.None);

        // STEP-05: Assert result.status is "missing_trip_details" and result.items.length is 0.
        result.GetProperty("status").GetString().Should().Be("missing_trip_details");
        result.GetProperty("items").GetArrayLength().Should().Be(0);
        // STEP-06: Assert context.TripDetailsMissing is true, context.State.TasteTargets equals a new AgentTasteTarget and context.Grants is empty.
        context.TripDetailsMissing.Should().BeTrue();
        context.State.TasteTargets.Should().Equal(new AgentTasteTarget("sour", .5));
        context.Grants.Should().BeEmpty();
        // STEP-07: Verify GetCandidateIdsAsync is never called and reco receives no other calls.
        fixture.Dishes.Verify(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        reco.VerifyNoOtherCalls();
    }

    // TEST-17: A whole-day taste the model proposes for "bữa 2 ngày 1" is narrowed to lunch of day 1 before the meal is ranked.
    /// <summary>Checks the backend binds a meal-specific taste to that meal even when the model widens it to the whole day.</summary>
    [Fact]
    public async Task ExecuteAsync_MealSpecificTasteWidenedByModel_IsNarrowedBeforeRanking()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        // STEP-02: Stub fixture.GetCandidateIdsAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1 });
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State, derive input from a copy of Input() and 1 more.
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["breakfast", "lunch", "dinner"],
            PendingPlanningRequest = new(["Tôi muốn ăn ở Hà Nội 2 ngày, tôi thích ăn chua", "Vừa, áp dụng cho bữa 2 ngày 1"]) };
        // Model không gửi được pendingPlanningRequest (ngoài schema tool); backend tự giữ bản đã lưu.
        var input = Input() with { State = context.State with { PendingPlanningRequest = null, TasteOverrides = [new(1, null, [new("sour", .5, "Vừa")])] } };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Run tools.ExecuteAsync with context.
        await tools.ExecuteAsync(context, Call(input), CancellationToken.None);

        // STEP-05: Assert context.State.TasteOverrides.Select(filtered) equals ((short)1, "lunch") and context.TripDetailsMissing is false.
        context.State.TasteOverrides.Select(x => (x.DayNumber, x.MealSlot)).Should().Equal(((short)1, "lunch"));
        context.TripDetailsMissing.Should().BeFalse();
    }

    // TEST-18: A per-meal search grants the meal quota to the model and keeps the rest of the ranked distinct names as a reserve grant
    // for the same position, which is never returned to the model.
    /// <summary>Keeps unused ranked dishes available for backend repeat repair without enlarging the model's payload.</summary>
    [Fact]
    public async Task ExecuteAsync_PerMealSearch_KeepsRestOfRankingAsHiddenReserve()
    {
        // STEP-01: Create lunchTag as ANGI.Domain.Entities.Tag, create mainTag as ANGI.Domain.Entities.Tag, get catalog from Select().ToArray and 1 more.
        var lunchTag = new ANGI.Domain.Entities.Tag { Id = 2, Code = "lunch", Name = "Bữa trưa", TagType = ANGI.Domain.Enums.TagType.MealTime };
        var mainTag = new ANGI.Domain.Entities.Tag { Id = 5, Code = "local_sf_main", Name = "Món Chính", TagType = ANGI.Domain.Enums.TagType.Category };
        var catalog = new[] { "Phở", "Bún chả", "Cơm tấm", "Bánh xèo", "Miến", "Bún riêu" }.Select((name, index) => Named(index + 1, name, mainTag, lunchTag)).ToArray();
        var fixture = AgentTestData.Validator(catalog);
        // STEP-02: Stub fixture.GetCandidateIdsAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1, 2, 3, 4, 5, 6 });
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { NumDays = 1, MealSlots = ["lunch"] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-05: Assert context.Grants.Single().DishIds equals 1.
        // Một ngày trong phạm vi: suất 1 + 2 = 3 món cho model, 3 món còn lại làm dự phòng.
        context.Grants.Single().DishIds.Should().Equal(1, 2, 3);
        // STEP-06: Get reserve from context.ReserveGrants.Single.
        var reserve = context.ReserveGrants.Single();
        // STEP-07: Assert reserve.DishIds equals 4, (reserve.DayNumber, reserve.MealSlot) is ((short?)null, "lunch") and 1 more.
        reserve.DishIds.Should().Equal(4, 5, 6);
        (reserve.DayNumber, reserve.MealSlot).Should().Be(((short?)null, "lunch"));
        result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("dishId").GetInt32()).Should().Equal(1, 2, 3);
    }

    // TEST-19: A dish with the same name as a dish kept outside the edited slot is not offered again, even at another restaurant.
    /// <summary>Prevents "món khác" from returning the same dish name from a different restaurant.</summary>
    [Fact]
    public async Task ExecuteAsync_EditedSlot_ExcludesSameNameAtOtherRestaurant()
    {
        // STEP-01: Create lunchTag as ANGI.Domain.Entities.Tag and create fixture through AgentTestData.Validator.
        var lunchTag = new ANGI.Domain.Entities.Tag { Id = 2, Code = "lunch", Name = "Bữa trưa", TagType = ANGI.Domain.Enums.TagType.MealTime };
        var fixture = AgentTestData.Validator(Named(1, "Phở", lunchTag), Named(2, "Phở", lunchTag), Named(4, "Bún", lunchTag));
        // STEP-02: Stub fixture.GetCandidateIdsAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1, 2, 4 });
        // STEP-03: Get context from AgentTestData.Context and create tools as AgentToolExecutor.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1) with { DishName = "Phở" }, AgentTestData.Item(3, "b", 2) with { DishName = "Cơm" }));
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Run tools.ExecuteAsync with context.
        await tools.ExecuteAsync(context, Call(Input() with { Scope = "slot", DayNumber = 2, MealSlot = "lunch" }), CancellationToken.None);

        // STEP-05: Assert context.Grants.Single().DishIds equals 4.
        context.Grants.Single().DishIds.Should().Equal(4);
    }

    // TEST-20: Malformed recommendation arguments from the model come back as invalid_arguments with the reason, without ranking or failing
    // the turn; covers topK 0 and 201, missing day, missing meal, another province, radius, fractional price, a negative
    // excluded ID, an unknown tag and a day beyond the trip.
    /// <summary>Lets the model correct a bad tool call within the turn instead of turning the chat into HTTP 503 or 409.</summary>
    [Theory]
    [InlineData("top-zero")][InlineData("top-large")][InlineData("day-missing")][InlineData("slot-missing")]
    [InlineData("province")][InlineData("radius")][InlineData("price")][InlineData("excluded-id")][InlineData("unknown-tag")][InlineData("day-beyond-trip")]
    public async Task ExecuteAsync_InvalidArguments_ReturnsErrorToModelWithoutRanking(string area)
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        // STEP-02: Mock IRecommendationService and IAgentTelemetry.
        var fixture = AgentTestData.Validator(); var reco = new Mock<IRecommendationService>(); var telemetry = new Mock<IAgentTelemetry>();
        // STEP-03: Get context from AgentTestData.Context, derive input from the expected value and create tools as AgentToolExecutor.
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

        // STEP-04: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(input), CancellationToken.None);

        // STEP-05: Assert result.status is "invalid_arguments", result.warnings[0] has a value and 1 more.
        result.GetProperty("status").GetString().Should().Be("invalid_arguments");
        result.GetProperty("warnings")[0].GetString().Should().NotBeNullOrWhiteSpace();
        if (area == "unknown-tag") result.GetProperty("warnings")[0].GetString().Should().Contain("Tag 99");
        // STEP-06: Assert context.ToolNotice is null and context.Grants is empty.
        context.ToolNotice.Should().BeNull(); context.Grants.Should().BeEmpty();
        // STEP-07: Verify reco receives no other calls and Record is called once.
        reco.VerifyNoOtherCalls();
        telemetry.Verify(x => x.Record("tool_argument_error", 7, It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<object?>()), Times.Once);
    }

    // TEST-21: An exact search_catalog match is kept only as an answer fact, never as a notice that replaces a clarification, while an
    // unfound name becomes both the turn notice and the catalog notice; covers a found and an unfound restaurant.
    /// <summary>Stops "Mình tìm thấy đúng món/quán bạn hỏi trên ANGI." from being sent as the reply to an unrelated question.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_SearchCatalog_FoundIsAnswerOnlyAndUnfoundIsNotice(bool found)
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator();
        // STEP-02: Mock ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository; stub catalog.SearchAsync.
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.SearchAsync("restaurant", "Lẩu Bò Mai", "Da Nang", 21, It.IsAny<CancellationToken>())).ReturnsAsync(found
            ? [new(575, "Lẩu Bò Mai", 575, "Lẩu Bò Mai", "Da Nang", "2 Tống Duy Tân", true)] : []);
        catalog.Setup(x => x.SearchAsync("restaurant", "Lẩu Bò Mai", null, 21, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        // STEP-03: Get context from AgentTestData.Context, create tools as AgentToolExecutor and create call as AgentToolCall.
        var context = AgentTestData.Context();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);
        var call = new AgentToolCall("s", "search_catalog", JsonSerializer.SerializeToElement(new AgentCatalogInput("restaurant", "Lẩu Bò Mai", "Da Nang"), AgentJson.Options));

        // STEP-04: Run tools.ExecuteAsync with context and call and keep result.
        var result = await tools.ExecuteAsync(context, call, CancellationToken.None);

        // STEP-05: Assert result.status is found ? "found" : "not_found", depending on found, context.ToolNotice is null, context.CatalogNotice is null and 4 more.
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

    // TEST-22: A named dish without a meal is placed on one fitting meal before ranking, so other meals keep their own candidates.
    /// <summary>Fixes "phải có món X" filtering every meal to X and ending in "no candidates for breakfast".</summary>
    [Fact]
    public async Task ExecuteAsync_NamedDishWithoutMeal_IsPlacedOnOneMeal()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator(Named(1, "Phở"), Named(2, "Bún"));
        // STEP-02: Mock ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository; stub fixture.GetCandidateIdsAsync and catalog.SearchAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DishCandidateFilter filter, int _, CancellationToken _) => filter.DishIds.Count > 0 ? [1] : new[] { 2 });
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.SearchAsync("dish", "Phở", "Da Nang", 21, It.IsAny<CancellationToken>())).ReturnsAsync([new(1, "Phở", 10, "Quán A", "Da Nang", "1 A", true)]);
        // STEP-03: Get context from AgentTestData.Context, set context.Request.Content to a test text, set context.State to a copy of context.State and 1 more.
        var context = AgentTestData.Context();
        context.Request.Content = "nhất định phải có món Phở";
        context.State = context.State with { MealSlots = ["breakfast", "lunch"], MealRequests = [new(null, null, "Phở", null, [])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);

        // STEP-04: Run tools.ExecuteAsync with context.
        await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-05: Assert context.State.MealRequests equals a new AgentMealRequest and context.PlacedRequests equals a new AgentMealRequest.
        context.State.MealRequests.Should().Equal(new AgentMealRequest(1, "lunch", "Phở", null, []));
        context.PlacedRequests.Should().Equal(new AgentMealRequest(1, "lunch", "Phở", null, []));
    }

    // TEST-23: A restaurant serving only dinner moves off a meal the backend chose; a meal the user named stays and reports no candidates.
    /// <summary>Fixes a replacement restaurant getting stuck on the lunch position its predecessor was placed on.</summary>
    [Theory]
    [InlineData("đổi sang quán Lẩu Bò", "dinner")]
    [InlineData("bữa trưa ngày 1 ăn ở quán Lẩu Bò", "lunch")]
    public async Task ExecuteAsync_RestaurantCannotServeMeal_MovesOnlyBackendPlacement(string content, string expected)
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator(Named(1, "Lẩu"));
        // STEP-02: Mock ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository; stub fixture.GetCandidateIdsAsync and catalog.SearchAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DishCandidateFilter filter, int _, CancellationToken _) => filter.CompatibleMealTimeCode == "dinner" ? [1] : Array.Empty<int>());
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.SearchAsync("restaurant", "Lẩu Bò", "Da Nang", 21, It.IsAny<CancellationToken>())).ReturnsAsync([new(10, "Lẩu Bò", 10, "Lẩu Bò", "Da Nang", "1 A", true)]);
        // STEP-03: Get context from AgentTestData.Context, set context.Request.Content to content, set context.State to a copy of context.State and 1 more.
        var context = AgentTestData.Context();
        context.Request.Content = content;
        context.State = context.State with { MealSlots = ["lunch", "dinner"], MealRequests = [new(1, "lunch", null, "Lẩu Bò", [])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);

        // STEP-04: Run tools.ExecuteAsync with context.
        await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-05: Assert context.State.MealRequests.Single().MealSlot is expected.
        context.State.MealRequests.Single().MealSlot.Should().Be(expected);
    }

    // TEST-24: An unresolved name keeps its wide request so the ranking step reports it as a catalog question.
    /// <summary>Ensures placement never hides a dish the catalog does not have.</summary>
    [Fact]
    public async Task ExecuteAsync_UnknownNamedDish_StaysUnplacedAndAsks()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator();
        // STEP-02: Mock ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository; stub catalog.SearchAsync.
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), 21, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        // STEP-03: Get context from AgentTestData.Context, set context.Request.Content to a test text, set context.State to a copy of context.State and 1 more.
        var context = AgentTestData.Context();
        context.Request.Content = "phải có món Phở Sao Hỏa";
        context.State = context.State with { MealSlots = ["lunch"], MealRequests = [new(null, null, "Phở Sao Hỏa", null, [])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);

        // STEP-04: Run tools.ExecuteAsync with context.
        await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-05: Assert context.PlacedRequests is empty and context.ToolNotice contains the expected text.
        context.PlacedRequests.Should().BeEmpty();
        context.ToolNotice.Should().Contain("Phở Sao Hỏa");
    }

    // TEST-25: Pointing words are refused as a catalog name before any lookup and never become a "not found" notice.
    /// <summary>Stops "cái này" from producing a false claim that the dish is not in the catalog.</summary>
    [Fact]
    public async Task ExecuteAsync_SearchPointingWords_ReturnsArgumentErrorWithoutLookup()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator();
        // STEP-02: Mock ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository.
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        // STEP-03: Get context from AgentTestData.Context, create tools as AgentToolExecutor and create call as AgentToolCall.
        var context = AgentTestData.Context();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);
        var call = new AgentToolCall("s", "search_catalog", JsonSerializer.SerializeToElement(new AgentCatalogInput("dish", "cái này"), AgentJson.Options));

        // STEP-04: Run tools.ExecuteAsync with context and call and keep result.
        var result = await tools.ExecuteAsync(context, call, CancellationToken.None);

        // STEP-05: Assert result.status is "invalid_arguments" and context.ToolNotice is null.
        result.GetProperty("status").GetString().Should().Be("invalid_arguments");
        context.ToolNotice.Should().BeNull();
        // STEP-06: Verify catalog receives no other calls.
        catalog.VerifyNoOtherCalls();
    }

    // TEST-26: Dishes the user replaced earlier are not ranked again; when they are the only candidates they are still offered.
    /// <summary>Prevents repeated "món khác" from cycling back while never leaving a meal without candidates.</summary>
    [Theory]
    [InlineData(new[] { "Bún" }, new[] { 1 })]
    [InlineData(new[] { "Phở", "Bún" }, new[] { 1, 2 })]
    public async Task ExecuteAsync_ReplacedDishes_ExcludedUnlessOnlyChoice(string[] replaced, int[] expected)
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator(Named(1, "Phở"), Named(2, "Bún"));
        // STEP-02: Stub fixture.GetCandidateIdsAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1, 2 });
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["lunch"], ReplacedDishNames = replaced };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-05: Assert result.items.EnumerateArray().Select(filtered) equals expected.
        result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("dishId").GetInt32()).Should().Equal(expected);
    }

    // TEST-27: One full search splits days with different places, each searched around its own place, while a day without one searches the whole province and keeps its own grant.
    /// <summary>Ensures the model needs one call for the whole itinerary and that candidates of one day never authorize another.</summary>
    [Fact]
    public async Task ExecuteAsync_ItineraryDays_SearchEachDayAroundItsPlace()
    {
        // STEP-01: Create fixture through AgentTestData.Validator and create filters as List<DishCandidateFilter>.
        var fixture = AgentTestData.Validator(Named(1, "Phở"), Named(2, "Bún"), Named(3, "Cơm"));
        var filters = new List<DishCandidateFilter>();
        // STEP-02: Stub fixture.GetCandidateIdsAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback((DishCandidateFilter filter, int _, CancellationToken _) => filters.Add(filter))
            .ReturnsAsync((DishCandidateFilter filter, int _, CancellationToken _) => filter.Area?.Latitude == 16.05m ? new[] { 1 } : filter.Area?.Latitude == 16.06m ? new[] { 2 } : new[] { 3 });
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { NumDays = 3, MealSlots = ["lunch"], DayAreas = [new(1, "A", 16.05m, 108.2m, 3m), new(2, "B", 16.06m, 108.2m, 5m)] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-05: Assert filters.Select(filtered) equals 3m, context.Grants.Select(filtered) equals ((short?)1, 1) and result.scopedResults.length is 3.
        filters.Select(filter => filter.Area?.RadiusKm).Should().Equal(3m, 5m, null);
        context.Grants.Select(grant => (grant.DayNumber, grant.DishIds.Single())).Should().Equal(((short?)1, 1), ((short?)2, 2), ((short?)3, 3));
        result.GetProperty("scopedResults").GetArrayLength().Should().Be(3);
    }

    // TEST-28: A place the itinerary holds is refused as a catalog name before any lookup, so the reply never claims it is "not found"; other names are still searched.
    /// <summary>Stops the failure of the reported conversation, where "Hoàn Kiếm" was searched as a restaurant name.</summary>
    [Fact]
    public async Task ExecuteAsync_SearchItineraryPlace_ReturnsArgumentErrorWithoutLookup()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator();
        // STEP-02: Mock ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository.
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State, create tools as AgentToolExecutor and 1 more.
        var context = AgentTestData.Context();
        context.State = context.State with { DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 3m)] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);
        var call = new AgentToolCall("s", "search_catalog", JsonSerializer.SerializeToElement(new AgentCatalogInput("restaurant", "hoàn kiếm"), AgentJson.Options));

        // STEP-04: Run tools.ExecuteAsync with context and call and keep result.
        var result = await tools.ExecuteAsync(context, call, CancellationToken.None);

        // STEP-05: Assert result.status is "invalid_arguments" and context.ToolNotice is null.
        result.GetProperty("status").GetString().Should().Be("invalid_arguments");
        context.ToolNotice.Should().BeNull();
        // STEP-06: Verify catalog receives no other calls.
        catalog.VerifyNoOtherCalls();
    }

    // TEST-29: A named place offers the nearer of two dishes with nearly equal scores first, asks reco for the deep list, and keeps a clearly better taste on top; covers a day of the itinerary and a whole-trip area.
    /// <summary>Ensures the near-first rule reaches the model's list and the grant order the backend repairs from.</summary>
    [Theory]
    [InlineData("day")]
    [InlineData("trip")]
    public async Task ExecuteAsync_NamedPlace_PrefersNearerAmongNearlyEqual(string area)
    {
        // STEP-01: Unpack (tools, reco, context) from NearbyScenario(), set context.State to a copy of context.State and set context.State to a test value.
        var (tools, reco, context) = NearbyScenario();
        context.State = context.State with { MealSlots = ["lunch"], NumDays = 1 };
        context.State = area == "day"
            ? context.State with { DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 5m)] }
            : context.State with { OriginMode = "pinned", OriginLatitude = 16.05m, OriginLongitude = 108.2m, OriginLabel = "Hoàn Kiếm", RadiusKm = 5m, NamedAreaLabel = "Hoàn Kiếm" };

        // STEP-02: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-03: Assert result.items.EnumerateArray().Select(filtered) equals 2 and context.Grants.Single().DishIds equals 2.
        result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("dishId").GetInt32()).Should().Equal(2, 1, 3);
        context.Grants.Single().DishIds.Should().Equal(2, 1, 3);
        // STEP-04: Verify RecommendConversationAsync is called once.
        reco.Verify(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), new AgentLimits().MaxRanked, It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-30: Every case already handled keeps reco's order and depth; covers the whole province, GPS, a pinned map origin and a label that no longer matches the origin.
    /// <summary>Guards the user's rule that this new case must not change what the system already does.</summary>
    [Theory]
    [InlineData("province")]
    [InlineData("gps")]
    [InlineData("pinned")]
    [InlineData("stale-label")]
    public async Task ExecuteAsync_NoNamedPlace_KeepsRecoOrder(string area)
    {
        // STEP-01: Unpack (tools, reco, context) from NearbyScenario(), set context.State to a copy of context.State and set context.State to a test value.
        var (tools, reco, context) = NearbyScenario();
        context.State = context.State with { MealSlots = ["lunch"], NumDays = 1 };
        context.State = area switch
        {
            "gps" => context.State with { OriginMode = "gps", OriginLatitude = 16.05m, OriginLongitude = 108.2m, OriginLabel = "Vị trí hiện tại", RadiusKm = 5m },
            "pinned" => context.State with { OriginMode = "pinned", OriginLatitude = 16.05m, OriginLongitude = 108.2m, OriginLabel = "Nhà", RadiusKm = 5m },
            "stale-label" => context.State with { OriginMode = "gps", OriginLatitude = 16.05m, OriginLongitude = 108.2m, OriginLabel = "Vị trí hiện tại", RadiusKm = 5m, NamedAreaLabel = "Hoàn Kiếm" },
            _ => context.State
        };

        // STEP-02: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-03: Assert result.items.EnumerateArray().Select(filtered) equals 1.
        result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("dishId").GetInt32()).Should().Equal(1, 2, 3);
        // STEP-04: Verify RecommendConversationAsync is called once.
        reco.Verify(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), 30, It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-31: A rating fallback has no taste score to compare, so a named place keeps its order.
    /// <summary>Ensures distance never replaces the rating order when reco did not rank the dishes.</summary>
    [Fact]
    public async Task ExecuteAsync_NamedPlaceWithRatingFallback_KeepsOrder()
    {
        // STEP-01: Unpack (tools, _, context) from NearbyScenario() and set context.State to a copy of context.State.
        var (tools, _, context) = NearbyScenario();
        context.State = context.State with { MealSlots = ["lunch"], NumDays = 1, Preferences = null, DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 5m)] };

        // STEP-02: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-03: Assert result.items.EnumerateArray().Select(filtered) equals 1.
        result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("dishId").GetInt32()).Should().Equal(1, 2, 3);
    }

    // TEST-32: A named place sends reco the nearest branch of a dish; without one the first-rated branch stays, as before.
    /// <summary>Ensures the same dish is offered close to the user only in the new case.</summary>
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task ExecuteAsync_SameDishBranches_NearestOnlyForNamedPlace(bool named, int expected)
    {
        // STEP-01: Get far from Named, set far.Restaurant.Latitude to 16.08m, get near from Named, set near.Restaurant.Latitude to 16.051m and 1 more.
        var far = Named(1, "Phở"); far.Restaurant.Latitude = 16.08m;
        var near = Named(2, "Phở"); near.Restaurant.Latitude = 16.051m;
        var fixture = AgentTestData.Validator(far, near);
        // STEP-02: Stub fixture.GetCandidateIdsAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1, 2 });
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["lunch"], NumDays = 1, DayAreas = named ? [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 5m)] : null };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-05: Assert result.items.EnumerateArray().Single().dishId is expected.
        result.GetProperty("items").EnumerateArray().Single().GetProperty("dishId").GetInt32().Should().Be(expected);
    }

    // TEST-33: A meal with no dish in its named place searches further step by step, stops at the first step with a dish and records it; the day's other meals keep their radius.
    /// <summary>Implements the chosen rule: only the empty meal widens, with no question and no extra model call.</summary>
    [Fact]
    public async Task ExecuteAsync_MealWithoutDishInNamedPlace_WidensOnlyThatMeal()
    {
        // STEP-01: Unpack (tools, filters, context) from WideningScenario() and set context.State to a copy of context.State.
        var (tools, filters, context) = WideningScenario((filter) => filter.CompatibleMealTimeCode == "lunch" || filter.Area?.RadiusKm >= 8m ? new[] { filter.CompatibleMealTimeCode == "lunch" ? 2 : 1 } : []);
        context.State = context.State with { NumDays = 1, MealSlots = ["breakfast", "lunch"], DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 3m)] };

        // STEP-02: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-03: Assert filters.Where(filtered).Select(filtered) equals 3m, context.State.WidenedMeals equals a new AgentWidenedMeal and 2 more.
        filters.Where(filter => filter.CompatibleMealTimeCode == "breakfast").Select(filter => filter.Area!.RadiusKm).Should().Equal(3m, 5m, 8m);
        filters.Where(filter => filter.CompatibleMealTimeCode == "lunch").Select(filter => filter.Area!.RadiusKm).Should().Equal(3m);
        context.State.WidenedMeals.Should().Equal(new AgentWidenedMeal(1, "breakfast", 8m));
        context.Grants.Single(grant => grant.MealSlot == "breakfast").Constraints.WidenedMeals.Should().Equal(new AgentWidenedMeal(1, "breakfast", 8m));
        result.GetProperty("mealCoverage").EnumerateArray().Should().OnlyContain(meal => meal.GetProperty("availableCount").GetInt32() > 0);
    }

    // TEST-34: The same search keeps the old behaviour everywhere else; covers the whole province, GPS and a map origin.
    /// <summary>Guards the user's rule that the new case must not change meals already handled: they report a gap instead of widening.</summary>
    [Theory]
    [InlineData("province")]
    [InlineData("gps")]
    [InlineData("pinned")]
    public async Task ExecuteAsync_EmptyMealWithoutNamedPlace_DoesNotWiden(string area)
    {
        // STEP-01: Unpack (tools, filters, context) from WideningScenario(), set context.State to a copy of context.State and set context.State to a test value.
        var (tools, filters, context) = WideningScenario(_ => []);
        context.State = context.State with { NumDays = 1, MealSlots = ["breakfast"] };
        context.State = area switch
        {
            "gps" => context.State with { OriginMode = "gps", OriginLatitude = 16.05m, OriginLongitude = 108.2m, OriginLabel = "Vị trí hiện tại", RadiusKm = 3m },
            "pinned" => context.State with { OriginMode = "pinned", OriginLatitude = 16.05m, OriginLongitude = 108.2m, OriginLabel = "Nhà", RadiusKm = 3m },
            _ => context.State
        };

        // STEP-02: Run tools.ExecuteAsync with context.
        await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-03: Assert filters has exactly one matching item and context.State.WidenedMeals is null.
        filters.Should().ContainSingle();
        context.State.WidenedMeals.Should().BeNull();
    }

    // TEST-35: With nothing even at the largest radius the meal is reported as a real gap, and no wider radius is recorded.
    /// <summary>Ensures widening has an end, and that the existing coverage question still covers the last case.</summary>
    [Fact]
    public async Task ExecuteAsync_NoDishAtLargestRadius_ReportsGap()
    {
        // STEP-01: Unpack (tools, filters, context) from WideningScenario() and set context.State to a copy of context.State.
        var (tools, filters, context) = WideningScenario(_ => []);
        context.State = context.State with { NumDays = 1, MealSlots = ["breakfast"], DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 30m)] };

        // STEP-02: Run tools.ExecuteAsync with context.
        await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-03: Assert filters.Select(filtered) equals 30m, context.State.WidenedMeals is null and context.MealCoverage.Single().AvailableCount is 0.
        filters.Select(filter => filter.Area!.RadiusKm).Should().Equal(30m, 50m);
        context.State.WidenedMeals.Should().BeNull();
        context.MealCoverage.Single().AvailableCount.Should().Be(0);
    }

    // TEST-36: A later search that finds dishes inside the radius drops the recorded widening, and only for that meal.
    /// <summary>Ensures a stale wider radius cannot keep allowing far dishes once nearer ones exist.</summary>
    [Fact]
    public async Task ExecuteAsync_DishesBackInRadius_ClearsThatMealsWidening()
    {
        // STEP-01: Unpack (tools, _, context) from WideningScenario() and set context.State to a copy of context.State.
        var (tools, _, context) = WideningScenario(_ => new[] { 1 });
        context.State = context.State with { NumDays = 1, MealSlots = ["lunch"], DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 3m)],
            WidenedMeals = [new(1, "lunch", 8m), new(1, "dinner", 12m)] };

        // STEP-02: Run tools.ExecuteAsync with context.
        await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-03: Assert context.State.WidenedMeals equals a new AgentWidenedMeal.
        context.State.WidenedMeals.Should().Equal(new AgentWidenedMeal(1, "dinner", 12m));
    }

    // TEST-37: A dish requested by name is not searched further out, because a wider circle cannot make a different dish the one the user asked for.
    /// <summary>Keeps the existing "thiếu món" answer for named dishes inside a named place.</summary>
    [Fact]
    public async Task ExecuteAsync_NamedDishMissingInPlace_DoesNotWiden()
    {
        // STEP-01: Create fixture through AgentTestData.Validator and create filters as List<DishCandidateFilter>.
        var fixture = AgentTestData.Validator(Named(1, "Phở"));
        var filters = new List<DishCandidateFilter>();
        // STEP-02: Mock ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository; stub fixture.GetCandidateIdsAsync and catalog.SearchAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback((DishCandidateFilter filter, int _, CancellationToken _) => filters.Add(filter)).ReturnsAsync(Array.Empty<int>());
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.SearchAsync("dish", "Phở", It.IsAny<string?>(), 21, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AgentCatalogMatch(1, "Phở", 1, "Quán", "Da Nang", "Địa chỉ", true)]);
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { NumDays = 1, MealSlots = ["breakfast", "lunch"], DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 3m)], MealRequests = [new(1, "lunch", "Phở", null, [])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(),
            new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);

        // STEP-04: Run tools.ExecuteAsync with context.
        await tools.ExecuteAsync(context, Call(Input()), CancellationToken.None);

        // STEP-05: Assert filters.Where(filtered).Select(filtered) equals 3m and context.State.WidenedMeals ?? [] holds no matching item.
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

    // TEST-38: A taste level the user never stated is refused before any catalog read or ranking and leaves the state unchanged; covers no quote, a quote nobody wrote and a quote naming only the taste.
    /// <summary>Conversation 10 got a level the model invented; the model has to ask instead of ranking with it.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("cay vừa")]
    [InlineData("thích ăn cay")]
    public async Task ExecuteAsync_TasteLevelNotStated_IsRefusedWithoutRanking(string? quote)
    {
        // STEP-01: Create fixture through AgentTestData.Validator, get context from AgentTestData.Context and set its message.
        var fixture = AgentTestData.Validator();
        var context = AgentTestData.Context();
        context.Request.Content = "tôi thích ăn cay";
        var before = context.State;
        // STEP-02: Derive input from a copy of Input() and create tools as AgentToolExecutor.
        var input = Input() with { State = context.State with { TasteTargets = [new("spicy", .75, quote)] } };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-03: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Call(input), CancellationToken.None);

        // STEP-04: Assert result.status is "invalid_arguments", the warning tells the model to ask and context.State is unchanged.
        result.GetProperty("status").GetString().Should().Be("invalid_arguments");
        result.GetProperty("warnings")[0].GetString().Should().Contain("taste_intensity");
        context.State.Should().BeSameAs(before);
        // STEP-05: Verify GetCandidateIdsAsync is never called.
        fixture.Dishes.Verify(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-39: An unknown taste dimension is refused by the strict schema before any catalog read or ranking.
    /// <summary>Stops a made-up dimension from reaching the vector, where it has no position.</summary>
    [Fact]
    public async Task ExecuteAsync_SuggestDishUnknownDimension_IsRefused()
    {
        // STEP-01: Create fixture through AgentTestData.Validator and create tools as AgentToolExecutor.
        var fixture = AgentTestData.Validator();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        // STEP-02: Create call stating a dimension outside the ontology.
        var call = new AgentToolCall("s", "suggest_dish", JsonSerializer.SerializeToElement(new AgentSuggestionInput(TagIds: [], ExcludeTagIds: [], TasteTargets: [new("smoky", 1)]), AgentJson.Options));

        // STEP-03: Wrap tools.ExecuteAsync in act.
        var act = () => tools.ExecuteAsync(AgentTestData.Context(), call, CancellationToken.None);

        // STEP-04: Assert act throws ServiceUnavailableException.
        await act.Should().ThrowAsync<ServiceUnavailableException>();
        // STEP-05: Verify GetCandidateIdsAsync is never called.
        fixture.Dishes.Verify(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-40: A price ceiling written in thousands goes back to the model before any catalog read; covers a suggestion and a recommendation.
    /// <summary>Run as given, "50" for 50k filters out every dish and the user is told nothing fits.</summary>
    [Theory]
    [InlineData("suggest_dish")]
    [InlineData("get_recommendation")]
    public async Task ExecuteAsync_PriceInThousands_IsSentBack(string tool)
    {
        // STEP-01: Create fixture through AgentTestData.Validator and create tools as AgentToolExecutor.
        var fixture = AgentTestData.Validator();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        var context = AgentTestData.Context();
        // STEP-02: Create the call with a ceiling of 50.
        var call = tool == "suggest_dish"
            ? new AgentToolCall("s", tool, JsonSerializer.SerializeToElement(new AgentSuggestionInput(TagIds: [], ExcludeTagIds: [], MaxPrice: 50, TasteTargets: []), AgentJson.Options))
            : Call(Input() with { MaxPricePerMeal = 50m });

        // STEP-03: Run tools.ExecuteAsync and keep result.
        var result = await tools.ExecuteAsync(context, call, CancellationToken.None);
        // STEP-04: Assert the model is told the unit, nothing was suggested and the catalog was not read.
        result.GetProperty("status").GetString().Should().Be("invalid_arguments");
        result.GetProperty("warnings")[0].GetString().Should().Contain("50k là 50000");
        context.DishSuggestion.Should().BeNull();
        fixture.Dishes.Verify(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-41: A relaxed condition the model sends with get_recommendation is applied at once, with nothing staged and no exception that would turn into a fixed backend reply (conversation 223).
    /// <summary>Keeps the tool path in line with the model's own state, which is applied as written.</summary>
    [Fact]
    public async Task ExecuteAsync_RelaxedStateInRecommendation_IsAppliedWithoutStaging()
    {
        // STEP-01: Create fixture through AgentTestData.Validator, create tools, and a context whose trip excludes tag 5.
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<int>());
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        var context = AgentTestData.Context(); context.State = context.State with { ExcludeTagIds = [5] };

        // STEP-02: Wrap tools.ExecuteAsync with a get_recommendation call that drops the excluded tag in act.
        var act = () => tools.ExecuteAsync(context, Call(Input() with { State = context.State with { ExcludeTagIds = [] } }), CancellationToken.None);

        // STEP-03: Assert act does not throw, the tag is no longer excluded and no change waits for confirmation.
        await act.Should().NotThrowAsync();
        context.State.ExcludeTagIds.Should().BeEmpty(); context.State.PendingStateChange.Should().BeNull();
    }

    // TEST-42: A suggestion counts as personalized only when the user has a taste profile and reco confirms ranking with it; covers a real vector, an empty profile, a zero vector, a rating fallback and an unavailable profile.
    /// <summary>The model may only say "theo khẩu vị của bạn" when that is what actually happened.</summary>
    [Theory]
    [InlineData("vector", "conversation", true)]
    [InlineData("none", "conversation", false)]
    [InlineData("zero", "conversation", false)]
    [InlineData("vector", null, false)]
    [InlineData("unavailable", null, false)]
    public async Task ExecuteAsync_SuggestDish_ReportsWhetherRankedByTaste(string profile, string? vectorSource, bool personalized)
    {
        // STEP-01: Get dish from AgentTestData.Dish, create fixture through AgentTestData.Validator and stub GetCandidateIdsAsync.
        var dish = AgentTestData.Dish(221);
        var fixture = AgentTestData.Validator(dish);
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 221 });
        // STEP-02: Get context from AgentTestData.Context, set an empty state and mock IRecommendationService; stub GetPreferencesAsync and RecommendConversationAsync.
        var context = AgentTestData.Context();
        context.State = new AgentState();
        var reco = new Mock<IRecommendationService>();
        var vector = profile switch { "vector" => Enumerable.Repeat(0.5, 12).ToArray(), "zero" => new double[12], _ => null };
        if (profile != "unavailable") reco.Setup(x => x.GetPreferencesAsync(context.UserId, It.IsAny<CancellationToken>())).ReturnsAsync(new UserPreferenceSnapshot(context.UserId, 1, vector, null));
        reco.Setup(x => x.RecommendConversationAsync(context.UserId, It.IsAny<IReadOnlyList<int>>(), 1, It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(null, [221], [], null, vectorSource));
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-03: Run tools.ExecuteAsync with a suggest_dish call and keep result.
        var result = await tools.ExecuteAsync(context, SuggestCall(), CancellationToken.None);

        // STEP-04: Assert the tool result and context.DishSuggestion carry the dish's facts and the expected ranking.
        result.GetProperty("status").GetString().Should().Be("suggested");
        result.GetProperty("personalized").GetBoolean().Should().Be(personalized);
        result.GetProperty("dishes")[0].GetProperty("name").GetString().Should().Be(dish.Name);
        result.GetProperty("dishes")[0].GetProperty("restaurant").GetString().Should().Be(dish.Restaurant.Name);
        result.GetProperty("dishes")[0].GetProperty("price").GetDecimal().Should().Be(dish.Price);
        context.DishSuggestion!.Personalized.Should().Be(personalized);
    }

    // TEST-43: With no dish found the tool says so to the model, carries no dish and keeps the fixed line for a failed wording; the ranking service is not asked.
    /// <summary>Tells the model there is nothing to word, instead of letting it make something up.</summary>
    [Fact]
    public async Task ExecuteAsync_SuggestDishNothingFound_ReportsNothingFound()
    {
        // STEP-01: Create fixture through AgentTestData.Validator, stub GetCandidateIdsAsync and mock IRecommendationService.
        var fixture = AgentTestData.Validator();
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<int>());
        var reco = new Mock<IRecommendationService>();
        // STEP-02: Get context from AgentTestData.Context and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-03: Run tools.ExecuteAsync with a suggest_dish call and keep result.
        var result = await tools.ExecuteAsync(context, SuggestCall(), CancellationToken.None);

        // STEP-04: Assert the status, the empty dishes and the fixed reply.
        result.GetProperty("status").GetString().Should().Be("nothing_found");
        result.GetProperty("dishes").GetArrayLength().Should().Be(0);
        context.DishSuggestion!.FixedReply.Should().Be(AgentDishSuggestionPolicy.NothingFound);
        // STEP-05: Verify RecommendConversationAsync is never called.
        reco.Verify(x => x.RecommendConversationAsync(It.IsAny<int>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<int>(), It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-44: A place named for a suggestion narrows the search around it inside its province, and the reply is told where it searched.
    /// <summary>Conversation 158 kept suggesting Hai Bà Trưng and Hà Đông to someone who said they live in Cầu Giấy, because the tool could not take a place.</summary>
    [Fact]
    public async Task ExecuteAsync_SuggestDishWithArea_SearchesAroundIt()
    {
        // STEP-01: Create fixture through AgentTestData.Validator and capture the filter of GetCandidateIdsAsync.
        var fixture = AgentTestData.Validator(AgentTestData.Dish(221));
        var filters = new List<DishCandidateFilter>();
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(Capture.In(filters), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 221 });
        var context = AgentTestData.Context();
        context.State = new AgentState();

        // STEP-02: Run tools.ExecuteAsync with a suggest_dish call naming the area.
        var result = await PlaceTools(fixture.Dishes.Object, fixture.Tags.Object, fixture.Routes.Object).ExecuteAsync(context, SuggestCallIn("Cầu Giấy"), CancellationToken.None);

        // STEP-03: Assert the filter stays around the place inside its province and the result names it.
        filters.Single().Area.Should().NotBeNull();
        filters.Single().Province.Should().Be("Hà Nội");
        result.GetProperty("area").GetString().Should().Be("Cầu Giấy");
        result.GetProperty("areaNotFound").ValueKind.Should().Be(JsonValueKind.Null);
        context.DishSuggestion!.Area.Should().Be("Cầu Giấy");
        context.DishSuggestion.FixedReply.Should().Contain("quanh Cầu Giấy");
    }

    // TEST-45: A place that cannot be located is reported rather than ignored in silence, and a province is no place to search around.
    /// <summary>The user must be told the dish is not filtered by what they said.</summary>
    [Theory]
    [InlineData("abcde xyz", "abcde xyz")]
    [InlineData("Hà Nội", null)]
    public async Task ExecuteAsync_SuggestDishAreaNotLocated_SearchesWithoutIt(string area, string? notFound)
    {
        // STEP-01: Create fixture through AgentTestData.Validator and capture the filter of GetCandidateIdsAsync.
        var fixture = AgentTestData.Validator(AgentTestData.Dish(221));
        var filters = new List<DishCandidateFilter>();
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(Capture.In(filters), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 221 });
        var context = AgentTestData.Context();
        context.State = new AgentState();

        // STEP-02: Run tools.ExecuteAsync with a suggest_dish call naming the area.
        await PlaceTools(fixture.Dishes.Object, fixture.Tags.Object, fixture.Routes.Object).ExecuteAsync(context, SuggestCallIn(area), CancellationToken.None);

        // STEP-03: Assert the search has no circle and the suggestion reports what was not found.
        filters.Single().Area.Should().BeNull();
        context.DishSuggestion!.Area.Should().BeNull();
        context.DishSuggestion.AreaNotFound.Should().Be(notFound);
    }

    // TEST-46: A dish tagged for one meal says so when the user named no meal, and stays quiet when they named one.
    /// <summary>Conversation 158 offered a breakfast-only dish without saying so, and the user replied that they wanted lunch.</summary>
    [Theory]
    [InlineData(null, "bữa sáng")]
    [InlineData("breakfast", null)]
    public async Task ExecuteAsync_SuggestDish_StatesMealsOnlyWhenNoneWasNamed(string? mealSlot, string? meals)
    {
        // STEP-01: Create dish tagged for breakfast, then fixture through AgentTestData.Validator and stub GetCandidateIdsAsync.
        var dish = AgentTestData.Dish(221);
        dish.DishTags.Add(new DishTag { TagId = 9000, Tag = new Tag { Id = 9000, Code = "breakfast", TagType = ANGI.Domain.Enums.TagType.MealTime } });
        var fixture = AgentTestData.Validator(dish);
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 221 });
        var context = AgentTestData.Context();
        context.State = new AgentState();
        var call = new AgentToolCall("s", "suggest_dish", JsonSerializer.SerializeToElement(new AgentSuggestionInput(MealSlot: mealSlot, TagIds: [], ExcludeTagIds: [], TasteTargets: []), AgentJson.Options));

        // STEP-02: Run tools.ExecuteAsync with the call.
        await PlaceTools(fixture.Dishes.Object, fixture.Tags.Object, fixture.Routes.Object).ExecuteAsync(context, call, CancellationToken.None);

        // STEP-03: Assert context.DishSuggestion.Dishes[0].Meals is meals.
        context.DishSuggestion!.Dishes[0].Meals.Should().Be(meals);
    }

    // TEST-47: locate_places stores what it found on the state, grows the trip for a later day, keeps the user's note for the next question and never takes coordinates from the model.
    /// <summary>The model reads the itinerary; where each place is stays the backend's.</summary>
    [Fact]
    public async Task ExecuteAsync_LocatePlaces_StoresDaysAndNote()
    {
        // STEP-01: Create fixture through AgentTestData.Validator and get context with no plan.
        var fixture = AgentTestData.Validator();
        var context = AgentTestData.Context();
        context.State = new AgentState { NumDays = 2 };
        var call = LocateCall(null, new AgentLocateStop([1], "Cầu Giấy"), new AgentLocateStop([3], null));

        // STEP-02: Run tools.ExecuteAsync with the call.
        var result = await PlaceTools(fixture.Dishes.Object, fixture.Tags.Object, fixture.Routes.Object).ExecuteAsync(context, call, CancellationToken.None);

        // STEP-03: Assert the result and the stored state.
        result.GetProperty("status").GetString().Should().Be("located");
        context.State.Should().Match<AgentState>(x => x.Province == "Hà Nội" && x.NumDays == 3 && x.DayAreas!.Length == 1 && x.DayAreas[0].Label == "Cầu Giấy");
        result.GetProperty("wholeProvinceDays")[0].GetInt16().Should().Be(3);
        context.PlaceNote.Should().Contain("ngày 1 quanh Cầu Giấy");
    }

    // TEST-48: A whole-trip area becomes the search circle; a name nowhere on ANGI is reported as not found and changes nothing.
    /// <summary>"Cầu Giấy" for the whole trip and a place outside the data take different paths.</summary>
    [Theory]
    [InlineData("Cầu Giấy", "located")]
    [InlineData("Đà Nẵng", "not_found")]
    public async Task ExecuteAsync_LocatePlacesTripArea_SetsCircleOrReports(string area, string status)
    {
        // STEP-01: Create fixture through AgentTestData.Validator and get context with no plan.
        var fixture = AgentTestData.Validator();
        var context = AgentTestData.Context();
        context.State = new AgentState();

        // STEP-02: Run tools.ExecuteAsync with the call.
        var result = await PlaceTools(fixture.Dishes.Object, fixture.Tags.Object, fixture.Routes.Object).ExecuteAsync(context, LocateCall(area), CancellationToken.None);

        // STEP-03: Assert the status and the state.
        result.GetProperty("status").GetString().Should().Be(status);
        context.State.OriginMode.Should().Be(status == "located" ? "pinned" : "none");
        context.State.Province.Should().Be(status == "located" ? "Hà Nội" : null);
    }

    // TEST-49: Arguments outside the contract go back to the model without changing anything; covers nothing named, no days, a day out of range on each side and a name too long.
    /// <summary>Bad arguments are the model's to fix, and must not fail the turn.</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("nodays")]
    [InlineData("day0")]
    [InlineData("day101")]
    [InlineData("longname")]
    [InlineData("longarea")]
    public async Task ExecuteAsync_LocatePlacesInvalid_IsSentBack(string kind)
    {
        // STEP-01: Create fixture, context and the call of this kind.
        var fixture = AgentTestData.Validator();
        var context = AgentTestData.Context();
        context.State = new AgentState();
        var call = kind switch
        {
            "empty" => LocateCall(null), "nodays" => LocateCall(null, new AgentLocateStop([], "Cầu Giấy")),
            "day0" => LocateCall(null, new AgentLocateStop([0], "Cầu Giấy")), "day101" => LocateCall(null, new AgentLocateStop([101], "Cầu Giấy")),
            "longname" => LocateCall(null, new AgentLocateStop([1], new string('a', 101))), _ => LocateCall(new string('a', 101))
        };

        // STEP-02: Run tools.ExecuteAsync with the call.
        var result = await PlaceTools(fixture.Dishes.Object, fixture.Tags.Object, fixture.Routes.Object).ExecuteAsync(context, call, CancellationToken.None);

        // STEP-03: Assert the status and that the state did not change.
        result.GetProperty("status").GetString().Should().Be("invalid_arguments");
        context.State.Province.Should().BeNull();
        context.State.DayAreas.Should().BeNull();
    }

    /// <summary>Builds tools over a catalog with enough restaurants in Cầu Giấy, Hà Nội, and no map service.</summary>
    private static AgentToolExecutor PlaceTools(IDishRepository dishes, ITagRepository tags, IRouteService routes)
    {
        var catalog = new Mock<IAgentCatalogRepository>();
        catalog.Setup(x => x.GetProvincesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["Hà Nội"]);
        catalog.Setup(x => x.GetPublicPlacesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Enumerable.Range(0, 25)
            .Select(_ => new AgentCatalogPlace("Hà Nội", "1 Trần Thái Tông, P. Dịch Vọng, Cầu Giấy, Hà Nội", 21.03m, 105.79m)).ToArray());
        return new AgentToolExecutor(dishes, tags, Mock.Of<IRecommendationService>(), new(), new(routes), new(), Mock.Of<IAgentTelemetry>(),
            catalogSearch: catalog.Object, places: new AgentPlaceResolver(catalog.Object));
    }

    /// <summary>Builds the suggest_dish call a model makes around a place.</summary>
    private static AgentToolCall SuggestCallIn(string area) => new("s", "suggest_dish", JsonSerializer.SerializeToElement(new AgentSuggestionInput(TagIds: [], ExcludeTagIds: [], TasteTargets: [], Area: area), AgentJson.Options));

    /// <summary>Builds the locate_places call a model makes.</summary>
    private static AgentToolCall LocateCall(string? tripArea, params AgentLocateStop[] stops) => new("l", "locate_places", JsonSerializer.SerializeToElement(new AgentLocateInput(tripArea, stops), AgentJson.Options));

    /// <summary>Builds the suggest_dish call a model makes with no condition.</summary>
    private static AgentToolCall SuggestCall() => new("s", "suggest_dish", JsonSerializer.SerializeToElement(new AgentSuggestionInput(TagIds: [], ExcludeTagIds: [], TasteTargets: []), AgentJson.Options));

    /// <summary>Returns valid strict recommendation arguments with explicit empty lists.</summary>
    private static AgentRecommendationInput Input() => new(null, "Da Nang", "full", null, null, [], [], []);
    /// <summary>Serializes a complete strict tool call using the public camel-case contract.</summary>
    private static AgentToolCall Call(AgentRecommendationInput input) => new("c", "get_recommendation", JsonSerializer.SerializeToElement(input, AgentJson.Options));

    private static readonly NearbyArea Center = new(21.03m, 105.85m, 5m);

    // TEST-50: Inside one band the nearer dish comes first; a dish clearly better on taste stays ahead however far it is.
    /// <summary>Ensures the rule the user chose: a near dish only overtakes a dish of about the same taste.</summary>
    [Fact]
    public void OrderNearby_NearlyEqualScores_NearerFirst_ClearlyBetterTasteStaysAhead()
    {
        // STEP-01: Get far from At, get near from At, get clearlyWorseButNext from At and get ordered from AgentToolExecutor.OrderNearby.
        var far = At(1, 0.04m);
        var near = At(2, 0.001m);
        var clearlyWorseButNext = At(3, 0m);

        var ordered = AgentToolExecutor.OrderNearby([far, near, clearlyWorseButNext], new Dictionary<int, double> { [1] = .90, [2] = .87, [3] = .70 }, Center);

        // STEP-02: Assert ordered.Select(filtered) equals 2.
        ordered.Select(dish => dish.Id).Should().Equal(2, 1, 3);
    }

    // TEST-51: The band edge: exactly under the limit shares a band with the best dish, at the limit starts the next band; covers the boundary both ways.
    /// <summary>Pins the meaning of "gần bằng nhau" so a later change to it is deliberate.</summary>
    [Theory]
    [InlineData(.9, new[] { 2, 1 })]
    [InlineData(.851, new[] { 2, 1 })]
    [InlineData(.84, new[] { 1, 2 })]
    public void OrderNearby_BandEdge_DecidesWhetherDistanceCounts(double second, int[] expected)
    {
        // STEP-01: Get ordered from AgentToolExecutor.OrderNearby.
        var ordered = AgentToolExecutor.OrderNearby([At(1, 0.04m), At(2, 0.001m)], new Dictionary<int, double> { [1] = .9, [2] = second }, Center);

        // STEP-02: Assert ordered.Select(filtered) equals expected.
        ordered.Select(dish => dish.Id).Should().Equal(expected);
    }

    // TEST-52: Equal scores, as for an account with no taste survey, are ordered by distance alone; equal distance keeps the ranking.
    /// <summary>Ensures long ties cannot hide a near restaurant, and the order stays stable when distance cannot decide.</summary>
    [Fact]
    public void OrderNearby_AllTied_UsesDistanceThenKeepsRanking()
    {
        // STEP-01: Create scores as Dictionary<int, double>.
        var scores = new Dictionary<int, double> { [1] = .44, [2] = .44, [3] = .44 };

        // STEP-02: Assert AgentToolExecutor.OrderNearby().Select(filtered) equals 2 and AgentToolExecutor.OrderNearby().Select(filtered) equals 1.
        AgentToolExecutor.OrderNearby([At(1, 0.03m), At(2, 0.01m), At(3, 0.02m)], scores, Center).Select(dish => dish.Id).Should().Equal(2, 3, 1);
        AgentToolExecutor.OrderNearby([At(1, 0.01m), At(2, 0.01m)], new Dictionary<int, double> { [1] = .44, [2] = .44 }, Center).Select(dish => dish.Id).Should().Equal(1, 2);
    }

    // TEST-53: Dishes with no score stay after scored ones in their order; a list with none is returned unchanged; an empty list stays empty.
    /// <summary>Ensures missing data never invents a position.</summary>
    [Fact]
    public void OrderNearby_MissingScores_StayLastInOrder()
    {
        // STEP-01: Create scores as Dictionary<int, double>.
        var scores = new Dictionary<int, double> { [3] = .5 };

        // STEP-02: Assert AgentToolExecutor.OrderNearby().Select(filtered) equals 3, AgentToolExecutor.OrderNearby().Select(filtered) equals 1 and 1 more.
        AgentToolExecutor.OrderNearby([At(1, 0m), At(2, 0m), At(3, 0.04m)], scores, Center).Select(dish => dish.Id).Should().Equal(3, 1, 2);
        AgentToolExecutor.OrderNearby([At(1, 0.04m), At(2, 0m)], new Dictionary<int, double>(), Center).Select(dish => dish.Id).Should().Equal(1, 2);
        AgentToolExecutor.OrderNearby([], new Dictionary<int, double>(), Center).Should().BeEmpty();
    }

    // TEST-54: Branches of one dish reduce to the nearest while the dish keeps its first position; different dishes are untouched.
    /// <summary>Ensures "Phở" near the place is offered rather than the better-rated branch across the city.</summary>
    [Fact]
    public void NearestBranches_KeepsNearestAtFirstPosition()
    {
        // STEP-01: Get farBranch from At, set farBranch.Name to a test text, get other from At, set other.Name to a test text and 2 more.
        var farBranch = At(1, 0.04m); farBranch.Name = "Phở";
        var other = At(2, 0.02m); other.Name = "Bún";
        var nearBranch = At(3, 0.001m); nearBranch.Name = "Phở";

        // STEP-02: Assert NearestBranches(filtered).Select(filtered) equals 3.
        AgentToolExecutor.NearestBranches([farBranch, other, nearBranch], dish => dish.Name, Center).Select(dish => dish.Id).Should().Equal(3, 2);
    }

    // TEST-55: A taste stated in the request reaches reco for this ranking only, over the trip's taste and the profile; a level off the scale is dropped, state stays, and a user with no profile is not called personalized.
    /// <summary>"Gợi ý món chua" must change the ranking without changing the trip or claiming a match reco would not make for a user with no interactions.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_SuggestDishWithTaste_RanksByItWithoutSaving(bool hasProfile)
    {
        // STEP-01: Get dish from AgentTestData.Dish, create fixture through AgentTestData.Validator and stub GetCandidateIdsAsync.
        var dish = AgentTestData.Dish(221);
        var fixture = AgentTestData.Validator(dish);
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 221 });
        // STEP-02: Get context from AgentTestData.Context with a trip taste, mock IRecommendationService and keep the vector it receives.
        var context = AgentTestData.Context();
        context.State = new AgentState { TasteTargets = [new("spicy", .25)] };
        var before = context.State;
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.GetPreferencesAsync(context.UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserPreferenceSnapshot(context.UserId, 1, hasProfile ? Enumerable.Repeat(0.2, 12).ToArray() : null, null));
        RecommendationVector? sent = null;
        reco.Setup(x => x.RecommendConversationAsync(context.UserId, It.IsAny<IReadOnlyList<int>>(), 1, It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .Callback<int, IReadOnlyList<int>, int, RecommendationVector, CancellationToken>((_, _, _, vector, _) => sent = vector)
            .ReturnsAsync(new RecommendationResult(null, [221], [], null, "conversation"));
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        // STEP-03: Create call stating spicy and sour, plus a level off the scale.
        var call = new AgentToolCall("s", "suggest_dish", JsonSerializer.SerializeToElement(new AgentSuggestionInput(TagIds: [], ExcludeTagIds: [],
            TasteTargets: [new("spicy", .75), new("sour", .5), new("sweet", .3)]), AgentJson.Options));

        // STEP-04: Run tools.ExecuteAsync with context and call and keep result.
        var result = await tools.ExecuteAsync(context, call, CancellationToken.None);

        // STEP-05: Assert the vector carries the stated values, the rest come from the profile, and the off-scale level is dropped.
        var values = sent!.UserVectorOverride!;
        values[Array.IndexOf(AgentTasteOntology.Dimensions, "spicy")].Should().Be(.75);
        values[Array.IndexOf(AgentTasteOntology.Dimensions, "sour")].Should().Be(.5);
        values[Array.IndexOf(AgentTasteOntology.Dimensions, "sweet")].Should().Be(hasProfile ? 0.2 : 0);
        // STEP-06: Assert result.personalized follows the profile and context.State is unchanged.
        result.GetProperty("personalized").GetBoolean().Should().Be(hasProfile);
        context.State.Should().BeSameAs(before);
    }

    // TEST-56: A dish already in the plan is left out of a suggestion unless nothing else fits, and the tool says a plan exists; with no plan it says so.
    /// <summary>"Gợi ý 1 món" right after planning used to name a dish the plan already holds.</summary>
    [Theory]
    [InlineData(new[] { 1 }, 2, true)]
    [InlineData(new[] { 1, 2 }, 1, true)]
    [InlineData(new int[0], 1, false)]
    public async Task ExecuteAsync_SuggestDish_LeavesOutThePlansDishes(int[] planned, int expected, bool hasPlan)
    {
        // STEP-01: Create catalog of two dishes, create fixture through AgentTestData.Validator and stub GetCandidateIdsAsync.
        var catalog = new[] { AgentTestData.Dish(1), AgentTestData.Dish(2) };
        var fixture = AgentTestData.Validator(catalog);
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1, 2 });
        // STEP-02: Get context from a plan holding the planned dishes and create tools as AgentToolExecutor.
        var items = planned.Select(id => AgentTestData.Item() with { DishId = id, DishName = $"Dish {id}" }).ToArray();
        var context = AgentTestData.Context(hasPlan ? AgentTestData.Snapshot(items) : null);
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-03: Run tools.ExecuteAsync with a suggest_dish call and keep result.
        var result = await tools.ExecuteAsync(context, SuggestCall(), CancellationToken.None);

        // STEP-04: Assert the dish named and hasPlan.
        result.GetProperty("dishes")[0].GetProperty("name").GetString().Should().Be($"Dish {expected}");
        result.GetProperty("hasPlan").GetBoolean().Should().Be(hasPlan);
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
