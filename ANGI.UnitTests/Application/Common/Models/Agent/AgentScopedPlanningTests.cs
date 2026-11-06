using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.Services.Agent;
using ANGI.Application.Services.Agent.Proposals;
using ANGI.Application.UseCases.Validators.Agent;
using ANGI.UnitTests.Application.Services.Agent;
using ANGI.UnitTests.Application.UseCases.Agent;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Common.Models.Agent;

/// <summary>Checks scoped inheritance, named selectors, provenance and grounded replies at business boundaries.</summary>
public sealed class AgentScopedPlanningTests
{
    // TEST-01: A meal-level taste override wins over the day override without leaking into other days or the stored profile.
    /// <summary>Ensures scoped taste overrides apply only where the user asked for them.</summary>
    [Fact]
    public void Vector_MealOverridesDayWithoutLeakingToOtherDaysOrProfile()
    {
        // STEP-01: Get baseline from Enumerable.Repeat().ToArray and derive state from a copy of AgentTestData.State().
        var baseline = Enumerable.Repeat(.25d, 12).ToArray();
        var state = AgentTestData.State() with { Preferences = new() { Initialized = true, BaselineVector = baseline },
            TasteTargets = [new("sweet", .5)], TasteOverrides = [new(1, null, [new("spicy", 1)]), new(1, "dinner", [new("spicy", 0)])] };
        // STEP-02: Assert AgentScopedPlanning.Vector()[5] is 1, AgentScopedPlanning.Vector()[5] is 0, AgentScopedPlanning.Vector()[5] is .25 and 2 more.
        AgentScopedPlanning.Vector(state, 1, "lunch")![5].Should().Be(1);
        AgentScopedPlanning.Vector(state, 1, "dinner")![5].Should().Be(0);
        AgentScopedPlanning.Vector(state, 2, "lunch")![5].Should().Be(.25);
        AgentScopedPlanning.Vector(state, 2, "lunch")![0].Should().Be(.5);
        baseline.Should().OnlyContain(x => x == .25);
    }

    // TEST-02: Specialty tags and novelty requests stay separate from taste targets for the scoped day and meal.
    /// <summary>Ensures specialty and novelty requests filter candidates without changing the taste vector.</summary>
    [Fact]
    public void Request_SpecialtyTagsAndNoveltyRemainSeparateFromTaste()
    {
        // STEP-01: Derive state from a copy of AgentTestData.State().
        var state = AgentTestData.State() with { NumDays = 3,
            TasteOverrides = [new(1, null, [new("spicy", 1)])],
            MealRequests = [new(2, null, null, null, [4]), new(3, null, null, null, [], true)] };
        // STEP-02: Assert AgentScopedPlanning.Targets() is empty, AgentScopedPlanning.Request().TagIds equals 4 and 1 more.
        AgentScopedPlanning.Targets(state, 2, "lunch").Should().BeEmpty();
        AgentScopedPlanning.Request(state, 2, "lunch").TagIds.Should().Equal((short)4);
        AgentScopedPlanning.Request(state, 3, "lunch").DifferentFromPlan.Should().BeTrue();
    }

    // TEST-03: Malformed scoped state is rejected; covers an out-of-range day, an unknown meal, duplicate overrides, an invalid intensity, null targets, null request tags, a negative tag id, null overrides and null meal requests.
    /// <summary>Guards ranking against scoped state that is out of range, duplicated or missing collections.</summary>
    [Theory]
    [InlineData("day")][InlineData("meal")][InlineData("duplicate")][InlineData("intensity")][InlineData("null-targets")]
    [InlineData("request-null-tags")][InlineData("request-negative-id")][InlineData("null-overrides")][InlineData("null-requests")]
    public void Validation_RejectsMalformedScopedState(string problem)
    {
        // STEP-01: Derive state from a copy of AgentTestData.State() and set state to a test value.
        var state = AgentTestData.State() with { TasteOverrides = [new(1, null, [new("spicy", .5)])] };
        state = problem switch
        {
            "day" => state with { TasteOverrides = [new(3, null, [])] },
            "meal" => state with { TasteOverrides = [new(1, "brunch", [])] },
            "duplicate" => state with { TasteOverrides = [state.TasteOverrides[0], state.TasteOverrides[0]] },
            "intensity" => state with { TasteOverrides = [new(1, null, [new("spicy", .4)])] },
            "null-targets" => state with { TasteOverrides = [new(1, null, null!)] },
            "request-null-tags" => state with { MealRequests = [new(1, null, null, null, null!)] },
            "request-negative-id" => state with { MealRequests = [new(1, null, null, "Restaurant", [], RestaurantId: -1)] },
            "null-overrides" => state with { TasteOverrides = null! },
            _ => state with { MealRequests = null! }
        };
        // STEP-02: Assert the validator.Validate().IsValid is false.
        new AgentStateValidator().Validate(state).IsValid.Should().BeFalse();
    }

    // TEST-04: Legacy state JSON without scoped fields reads with empty overrides, requests and meal slots and the default replan mode.
    /// <summary>Keeps conversations stored before scoped planning existed readable with safe defaults.</summary>
    [Fact]
    public void LegacyJson_ReadsWithNoScopedOverridesOrCoverage()
    {
        // STEP-01: Read state from the response; assert state.TasteOverrides is empty, state.MealRequests is empty, state.MealSlots is empty and 1 more.
        var state = AgentJson.Read<AgentState>("{\"province\":\"Da Nang\",\"numDays\":2,\"budgetAmount\":500000}");
        state.TasteOverrides.Should().BeEmpty(); state.MealRequests.Should().BeEmpty(); state.MealSlots.Should().BeEmpty();
        state.PreferenceApplication.Should().Be("replan");
    }

    // TEST-05: A broad recommendation across days with different taste overrides ranks each day with its own vector and day-only grants in one tool call.
    /// <summary>Ensures one model call is enough while a trip-wide ranking never mixes different taste vectors.</summary>
    [Fact]
    public async Task BroadRecommendation_DifferentDaysAreRankedSeparatelyInOneCall()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        // STEP-02: Create vectors as List<double[]?>; mock IRecommendationService; stub fixture.GetCandidateIdsAsync and reco.RecommendConversationAsync.
        var fixture = AgentTestData.Validator(AgentTestData.Dish()); var reco = new Mock<IRecommendationService>();
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1 });
        var vectors = new List<double[]?>();
        reco.Setup(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), It.IsAny<int>(), It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .Callback<int, IReadOnlyList<int>, int, RecommendationVector, CancellationToken>((_, _, _, vector, _) => vectors.Add(vector.UserVectorOverride))
            .ReturnsAsync(() => new RecommendationResult(Guid.NewGuid(), [1], [], [.9], "conversation"));
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { Preferences = new() { Initialized = true, BaselineVector = Enumerable.Repeat(.25, 12).ToArray() },
            TasteOverrides = [new(1, null, [new("spicy", 1)])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Run tools.ExecuteAsync with context and keep response.
        var response = await tools.ExecuteAsync(context, Recommend(new(TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [], TopK: 12)), default);

        // STEP-05: Assert vectors.Select(filtered) equals 1, context.Grants.Select(filtered) equals 1, response.TryGetProperty() is false and 1 more.
        vectors.Select(vector => vector![5]).Should().Equal(1, .25);
        context.Grants.Select(grant => grant.DayNumber).Should().Equal((short)1, (short)2);
        response.TryGetProperty("status", out _).Should().BeFalse();
        response.GetProperty("scopedResults").EnumerateArray()
            .Select(part => part.GetProperty("selectionScope").GetProperty("dayNumber").GetInt32()).Should().Equal(1, 2);
    }

    // TEST-06: A day-scoped recommendation sends the effective override vector to reco without changing the trip-wide default vector.
    /// <summary>Ensures a scoped ranking uses its override vector while the trip default stays untouched.</summary>
    [Fact]
    public async Task ScopedRecommendation_SendsEffectiveVectorWithoutChangingTripDefault()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        // STEP-02: Declare captured to capture a call argument; mock IRecommendationService; stub fixture.GetCandidateIdsAsync and reco.RecommendConversationAsync.
        var fixture = AgentTestData.Validator(AgentTestData.Dish()); var reco = new Mock<IRecommendationService>();
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1 });
        RecommendationVector? captured = null;
        reco.Setup(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), 12, It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .Callback<int, IReadOnlyList<int>, int?, RecommendationVector, CancellationToken>((_, _, _, vector, _) => captured = vector)
            .ReturnsAsync(new RecommendationResult(Guid.NewGuid(), [1], [], [.9], "conversation"));
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { Preferences = new() { Initialized = true, BaselineVector = Enumerable.Repeat(.25, 12).ToArray() },
            TasteOverrides = [new(1, null, [new("spicy", 1)])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        // STEP-04: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Recommend(new(Scope: "day", DayNumber: 1, TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [], TopK: 12)), default);
        // STEP-05: Assert captured.UserVectorOverride[5] is 1, context.State.Preferences.EffectiveVector[5] is .25 and result.returnedCount is 1.
        captured!.UserVectorOverride![5].Should().Be(1);
        context.State.Preferences!.EffectiveVector![5].Should().Be(.25);
        result.GetProperty("returnedCount").GetInt32().Should().Be(1);
    }

    // TEST-07: An unresolved named restaurant never falls back to other restaurants; covers not found, ambiguous, unavailable and outside the province.
    /// <summary>Ensures a restaurant the user named is never silently swapped for a different one.</summary>
    [Theory]
    [InlineData("not_found")][InlineData("ambiguous")][InlineData("unavailable")][InlineData("outside_province")]
    public async Task NamedRestaurant_UnresolvedLookupNeverFallsBackToOtherRestaurants(string outcome)
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        // STEP-02: Create match as AgentCatalogMatch; mock IRecommendationService and IAgentCatalogRepository; stub catalog.SearchAsync.
        var fixture = AgentTestData.Validator(); var reco = new Mock<IRecommendationService>(); var catalog = new Mock<IAgentCatalogRepository>();
        var match = new AgentCatalogMatch(11, "Quán A", 11, "Quán A", "Da Nang", "Đường A", outcome != "unavailable");
        catalog.Setup(x => x.SearchAsync("restaurant", "Quán A", "Da Nang", 21, It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome switch { "not_found" or "outside_province" => [], "ambiguous" => [match, match with { Id = 12, RestaurantId = 12 }], _ => [match] });
        catalog.Setup(x => x.SearchAsync("restaurant", "Quán A", null, 21, It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome == "outside_province" ? [match with { Province = "Ha Noi" }] : []);
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context(); context.State = context.State with { MealRequests = [new(null, null, null, "Quán A", [])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);
        // STEP-04: Run tools.ExecuteAsync with context and keep result.
        var result = await tools.ExecuteAsync(context, Recommend(new(TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [])), default);
        // STEP-05: Assert result.status is outcome and context.Grants is empty.
        result.GetProperty("status").GetString().Should().Be(outcome);
        // STEP-06: Verify reco receives no other calls and fixture receives no other calls.
        context.Grants.Should().BeEmpty(); reco.VerifyNoOtherCalls(); fixture.Dishes.VerifyNoOtherCalls();
    }

    // TEST-08: A named dish request restricts candidate ids and the restaurant before ranking.
    /// <summary>Ensures a named dish request only ranks the matching dish at the matching restaurant.</summary>
    [Fact]
    public async Task NamedDish_QueryRestrictsIdsAndRestaurantBeforeRanking()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        // STEP-02: Declare filter to capture a call argument; mock IAgentCatalogRepository; stub catalog.SearchAsync and fixture.GetCandidateIdsAsync.
        var fixture = AgentTestData.Validator(); var catalog = new Mock<IAgentCatalogRepository>();
        catalog.Setup(x => x.SearchAsync("restaurant", "Quán A", "Da Nang", 21, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new(11, "Quán A", 11, "Quán A", "Da Nang", "A", true)]);
        catalog.Setup(x => x.SearchAsync("dish", "Món X", "Da Nang", 21, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new(7, "Món X", 11, "Quán A", "Da Nang", "A", true), new(8, "Món X", 12, "Quán B", "Da Nang", "B", true)]);
        DishCandidateFilter? filter = null;
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback<DishCandidateFilter, int, CancellationToken>((value, _, _) => filter = value).ReturnsAsync(Array.Empty<int>());
        // STEP-03: Get context from AgentTestData.Context, set context.Request.Content to a test text, set context.State to a copy of context.State and 1 more.
        var context = AgentTestData.Context(); context.Request.Content = "mọi bữa đều ăn Món X"; context.State = context.State with { MealRequests = [new(null, null, "Món X", "Quán A", [])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);
        // STEP-04: Run tools.ExecuteAsync with context.
        await tools.ExecuteAsync(context, Recommend(new(TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [])), default);
        // STEP-05: Assert filter.RestaurantId is 11 and filter.DishIds equals 7.
        filter!.RestaurantId.Should().Be(11); filter.DishIds.Should().Equal(7);
    }

    // TEST-09: A retained dish whose taste scope changed needs a new grant, and the new grant updates the item's request identity.
    /// <summary>Ensures a kept dish is re-authorized under the new taste and attributed to the new reco request.</summary>
    [Fact]
    public async Task RetainedDish_ChangedTasteRequiresNewGrantAndUpdatesRequestIdentity()
    {
        // STEP-01: Create fixture through AgentTestData.Validator, get original from AgentTestData.Snapshot, derive state from a copy of original.State and 1 more.
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        var original = AgentTestData.Snapshot(AgentTestData.Item() with { RequestId = Guid.NewGuid() });
        var state = original.State with { TasteOverrides = [new(1, null, [new("spicy", 1)])] };
        var snapshot = original with { State = state };
        // STEP-02: Assert Validator.ValidateAsync().Validation.Errors is not empty.
        (await fixture.Validator.ValidateAsync(snapshot, original, false, false, default, [])).Validation.Errors.Should().NotBeEmpty();
        // STEP-03: Derive grant from a copy of AgentTestData.Grant().
        var grant = AgentTestData.Grant([1], 1, "lunch") with { Constraints = state };
        // STEP-04: Validate a copy of snapshot and keep ranked.
        var ranked = await fixture.Validator.ValidateAsync(snapshot with { Grants = [grant] }, original, false, false, default, [grant]);
        // STEP-05: Assert ranked.Validation.Errors is empty, ranked.Snapshot.Items[0].RequestId is grant.RequestId and 1 more.
        ranked.Validation.Errors.Should().BeEmpty(); ranked.Snapshot.Items[0].RequestId.Should().Be(grant.RequestId);
        ranked.Snapshot.Items[0].RankingFingerprint.Should().NotBeNull();
    }

    // TEST-10: A meal not affected by the change, or a future-only taste change, does not require reranking; covers both cases.
    /// <summary>Avoids needless reranking for meals that a taste change does not affect.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task UnaffectedOrFutureOnlyMeal_DoesNotRequireReranking(bool futureOnly)
    {
        // STEP-01: Get source from AgentTestData.Snapshot and derive changed from a copy of source.
        var source = AgentTestData.Snapshot(AgentTestData.Item(day: futureOnly ? (short)1 : (short)2));
        var changed = source with { State = source.State with { PreferenceApplication = futureOnly ? "future" : "replan",
            TasteOverrides = [new(1, null, [new("spicy", 1)])] } };
        // STEP-02: Validate changed and keep result.
        var result = await AgentTestData.Validator(AgentTestData.Dish()).Validator.ValidateAsync(changed, source, false, false, default, []);
        // STEP-03: Assert result.Validation.Errors is empty.
        result.Validation.Errors.Should().BeEmpty();
    }

    // TEST-11: Declared meal coverage rejects a plan missing its breakfast and dinner slots.
    /// <summary>Ensures a plan covers every meal slot the user declared.</summary>
    [Fact]
    public async Task DeclaredMealCoverage_RejectsMissingBreakfastAndDinner()
    {
        // STEP-01: Get snapshot from AgentTestData.Snapshot and set snapshot to a copy of snapshot.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        snapshot = snapshot with { State = snapshot.State with { NumDays = 1, MealSlots = ["breakfast", "lunch", "dinner"] } };
        // STEP-02: Validate snapshot and keep result.
        var result = await AgentTestData.Validator(AgentTestData.Dish()).Validator.ValidateAsync(snapshot, null, false, false, default);
        // STEP-03: Assert result.Validation.Errors contains a matching item.
        result.Validation.Errors.Should().Contain(x => x.Contains("breakfast")).And.Contain(x => x.Contains("dinner"));
    }

    // TEST-12: A named meal request rejects another dish even when that dish has a candidate grant.
    /// <summary>Ensures a candidate grant cannot override an explicit dish or restaurant request.</summary>
    [Fact]
    public async Task NamedRequest_ValidatorRejectsAnotherDishEvenWithCandidateGrant()
    {
        // STEP-01: Get snapshot from AgentTestData.Snapshot, derive state from a copy of snapshot.State and set snapshot to a copy of snapshot.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        var state = snapshot.State with { MealRequests = [new(1, "lunch", "Món được yêu cầu", null, [])] };
        snapshot = snapshot with { State = state, Grants = [AgentTestData.Grant() with { Constraints = state }] };
        // STEP-02: Validate snapshot and keep result.
        var result = await AgentTestData.Validator(AgentTestData.Dish()).Validator.ValidateAsync(snapshot, null, false, false, default);
        // STEP-03: Assert result.Validation.Errors contains a matching item.
        result.Validation.Errors.Should().Contain(x => x.Contains("món/quán"));
    }

    // TEST-13: A novelty request rejects an initial plan that repeats a dish in another meal.
    /// <summary>Ensures a novelty request is honoured by rejecting repeated dishes.</summary>
    [Fact]
    public async Task Novelty_InitialPlanCannotRepeatTheDishInAnotherMeal()
    {
        // STEP-01: Get snapshot from AgentTestData.Snapshot, derive state from a copy of snapshot.State and set snapshot to a copy of snapshot.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item(), AgentTestData.Item(key: "b", day: 2));
        var state = snapshot.State with { MealRequests = [new(2, null, null, null, [], true)] };
        snapshot = snapshot with { State = state, Grants = [AgentTestData.Grant() with { Constraints = state }] };
        // STEP-02: Validate snapshot and keep result.
        var result = await AgentTestData.Validator(AgentTestData.Dish()).Validator.ValidateAsync(snapshot, null, false, false, default);
        // STEP-03: Assert result.Validation.Errors contains a matching item.
        result.Validation.Errors.Should().Contain(x => x.Contains("bị lặp"));
    }

    // TEST-14: Confirming a re-evaluated retained dish keeps the new request id and rejects a tampered ranking fingerprint.
    /// <summary>Ensures confirmation keeps the re-evaluated attribution and detects a forged ranking fingerprint.</summary>
    [Fact]
    public async Task Confirm_ReevaluatedRetainedDishPreservesNewRequestAndDetectsTampering()
    {
        // STEP-01: Create fixture through AgentTestData.Validator, get source from AgentTestData.Snapshot, derive state from a copy of source.State and 2 more.
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        var source = AgentTestData.Snapshot(AgentTestData.Item() with { Price = 50000, SourceItemId = 8, RequestId = Guid.NewGuid() });
        var state = source.State with { TasteOverrides = [new(1, null, [new("spicy", 1)])] };
        var grant = AgentTestData.Grant([1], 1, "lunch") with { Constraints = state };
        var draft = source with { State = state, Grants = [grant], RetainedItems = source.Items };
        // STEP-02: Validate draft and keep reviewed.
        var reviewed = await fixture.Validator.ValidateAsync(draft, source, false, false, default, [grant]);
        // STEP-03: Assert reviewed.Validation.Errors is empty.
        reviewed.Validation.Errors.Should().BeEmpty();
        // STEP-04: Validate reviewed.Snapshot and keep confirmed.
        var confirmed = await fixture.Validator.ValidateAsync(reviewed.Snapshot, source, true, false, default);
        // STEP-05: Assert confirmed.Validation.Errors is empty and confirmed.Snapshot.Items[0].RequestId is grant.RequestId.
        confirmed.Validation.Errors.Should().BeEmpty(); confirmed.Snapshot.Items[0].RequestId.Should().Be(grant.RequestId);
        // STEP-06: Derive tampered from a copy of reviewed.Snapshot.
        var tampered = reviewed.Snapshot with { Items = [reviewed.Snapshot.Items[0] with { RankingFingerprint = "forged" }] };
        // STEP-07: Assert Validator.ValidateAsync().Validation.Errors is not empty.
        (await fixture.Validator.ValidateAsync(tampered, source, true, false, default)).Validation.Errors.Should().NotBeEmpty();
    }

    // TEST-15: A future-only taste answer creates a savable revision that keeps the current dishes without reranking.
    /// <summary>Ensures a taste change for future suggestions can be saved without rebuilding the current plan.</summary>
    [Fact]
    public async Task FutureTasteOnlyAnswer_CreatesSavableRevisionWithoutReranking()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, get source from AgentTestData.Snapshot and get context from AgentTestData.Context.
        var fixture = new AgentUseCaseFixture();
        var source = AgentTestData.Snapshot(AgentTestData.Item() with { Price = 50000 });
        var context = AgentTestData.Context(source); context.Request.Content = "ngày 1 cay rất nhiều, chỉ cho gợi ý sau";
        // STEP-02: Stub fixture.RunAsync.
        fixture.Llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer() with { State = context.State with {
                PreferenceApplication = "future", TasteOverrides = [new(1, null, [new("spicy", 1, "cay rất nhiều")])] } }));
        // STEP-03: Get result from fixture.Orchestrator().RunAsync.
        var result = await fixture.Orchestrator().RunAsync(context, default);
        // STEP-04: Assert result.Snapshot is not null, result.Snapshot.Items[0].DishId is source.Items[0].DishId and 4 more.
        result.Snapshot.Should().NotBeNull(); result.Snapshot!.Items[0].DishId.Should().Be(source.Items[0].DishId);
        result.Snapshot.State.PreferenceApplication.Should().Be("future"); result.State.PendingQuestion.Should().BeNull();
        context.Grants.Should().BeEmpty(); result.Reply.Should().Contain("gợi ý tiếp theo");
    }

    // TEST-16: A taste-update answer cannot claim the old proposal was updated when no new proposal was built.
    /// <summary>Prevents the reply from telling the user a plan changed when it did not.</summary>
    [Fact]
    public async Task TasteUpdateAnswer_CannotClaimOldProposalWasUpdated()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture and get context from AgentTestData.Context.
        var fixture = new AgentUseCaseFixture(); var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item())); context.Request.Content = "ngày 1 cay rất nhiều";
        // STEP-02: Stub fixture.RunAsync.
        fixture.Llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer("Đã cập nhật tất cả") with {
                State = context.State with { TasteOverrides = [new(1, null, [new("spicy", 1, "cay rất nhiều")])] } }));
        // STEP-03: Get result from fixture.Orchestrator().RunAsync.
        var result = await fixture.Orchestrator().RunAsync(context, default);
        // STEP-04: Assert result.Snapshot is null, result.State.PendingQuestion is not null and result.Reply contains the expected text.
        result.Snapshot.Should().BeNull(); result.State.PendingQuestion.Should().NotBeNull();
        result.Reply.Should().Contain("chưa có đề xuất cập nhật").And.NotContain("Đã cập nhật tất cả");
        // STEP-05: Verify RunAsync is called 2 times.
        fixture.Llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-17: Provider prose cannot invent user-visible catalog facts; covers answer and clarify actions.
    /// <summary>Prevents unverified model prose from showing invented restaurant facts to the user.</summary>
    [Theory]
    [InlineData("answer")][InlineData("clarify")]
    public async Task ProviderProse_CannotInventUserVisibleCatalogFacts(string action)
    {
        // STEP-01: Create fixture as AgentUseCaseFixture.
        var fixture = new AgentUseCaseFixture();
        // STEP-02: Stub fixture.RunAsync.
        fixture.Llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new() { Action = action, Reply = "Nhà hàng Bịa mở 24/24, miễn phí và chắc chắn không cay.", Clarification = action == "clarify" ? "taste_scope" : null }));
        // STEP-03: Get result from fixture.Orchestrator().RunAsync.
        var result = await fixture.Orchestrator().RunAsync(AgentTestData.Context(), default);
        // STEP-04: Assert result.Reply does not contain the expected text, depending on action == "clarify", result.Reply contains the expected text and 1 more.
        result.Reply.Should().NotContain("Bịa").And.NotContain("24/24").And.NotContain("miễn phí");
        if (action == "clarify") result.Reply.Should().Contain("toàn chuyến").And.Contain("ngày/bữa");
    }

    // TEST-18: Snack requests for day-1 lunch and dinner split that day into meal scopes, keep day 2 whole, and the resulting grants validate a complete plan.
    /// <summary>Reproduces conversation 16: one broad call must cover meals with different catalog requests without a second model round.</summary>
    [Fact]
    public async Task BroadRecommendation_HeterogeneousDaySplitsIntoMealScopesThatValidate()
    {
        // STEP-01: Unpack (tools, validator, _, context) from SnackFixture().
        var (tools, validator, _, context) = SnackFixture(candidatesFor: _ => [1, 2]);

        // STEP-02: Run tools.ExecuteAsync with context and keep response.
        var response = await tools.ExecuteAsync(context, Recommend(new(TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [], TopK: 30)), default);

        // STEP-03: Assert Select(filtered).Select(filtered) equals "1:breakfast", context.Grants.Select(filtered) equals "1:breakfast" and 1 more.
        response.GetProperty("scopedResults").EnumerateArray().Select(part => part.GetProperty("selectionScope"))
            .Select(scope => $"{scope.GetProperty("dayNumber").GetInt32()}:{scope.GetProperty("mealSlot").GetString()}")
            .Should().Equal("1:breakfast", "1:lunch", "1:dinner", "2:");
        context.Grants.Select(grant => $"{grant.DayNumber}:{grant.MealSlot}")
            .Should().Equal("1:breakfast", "1:lunch", "1:dinner", "2:breakfast", "2:lunch", "2:dinner");
        context.Grants.Where(grant => grant.DayNumber == 1 && grant.MealSlot is "lunch" or "dinner")
            .Should().OnlyContain(grant => grant.DishIds.SequenceEqual(new[] { 2 }));
        // STEP-04: Get items from Select().ToArray and create plan as AgentSnapshot.
        var items = new[] { (1, "breakfast", 1), (1, "lunch", 2), (1, "dinner", 2), (2, "breakfast", 1), (2, "lunch", 1), (2, "dinner", 1) }
            .Select((x, index) => AgentTestData.Item(x.Item3, $"k{index}", (short)x.Item1, x.Item2)).ToArray();
        var plan = new AgentSnapshot(context.State, items, context.Grants.ToArray());
        // STEP-05: Assert validator.ValidateAsync().Validation.Errors is empty.
        (await validator.ValidateAsync(plan, null, false, false, default, context.Grants)).Validation.Errors.Should().BeEmpty();
    }

    // TEST-19: A split scope with no snack candidates reports zero coverage for those meals while other meals still return; when every meal is empty the call returns no_candidates without grants.
    /// <summary>Ensures missing choices are reported per meal from evidence and never hide the meals that do have candidates.</summary>
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task BroadRecommendation_SplitScopeReportsEmptyMealsFromEvidence(bool everyMealEmpty)
    {
        // STEP-01: Unpack (tools, _, reco, context) from SnackFixture().
        var (tools, _, reco, context) = SnackFixture(candidatesFor: filter => everyMealEmpty || filter.TagIds.Contains((short)6) ? [] : [1]);

        // STEP-02: Run tools.ExecuteAsync with context and keep response.
        var response = await tools.ExecuteAsync(context, Recommend(new(TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [], TopK: 30)), default);

        // STEP-03: Assert depending on everyMealEmpty, response.status is "no_candidates", context.Grants is empty and 3 more; verify reco receives no other calls.
        if (everyMealEmpty)
        {
            response.GetProperty("status").GetString().Should().Be("no_candidates");
            context.Grants.Should().BeEmpty(); reco.VerifyNoOtherCalls();
        }
        else
        {
            response.GetProperty("mealCoverage").EnumerateArray()
                .Where(meal => meal.GetProperty("dayNumber").GetInt32() == 1 && meal.GetProperty("mealSlot").GetString() is "lunch" or "dinner")
                .Should().HaveCount(2).And.OnlyContain(meal => meal.GetProperty("availableCount").GetInt32() == 0);
            response.GetProperty("returnedCount").GetInt32().Should().Be(1);
            context.ToolNotice.Should().BeNull();
        }
    }

    // TEST-20: Branches sharing a dish name (ignoring case and spacing) reach reco once, as the first branch in rating order, so reco ranks distinct dishes.
    /// <summary>Prevents hundreds of copies of a few catalog dishes from filling every ranked slot with the same names.</summary>
    [Fact]
    public async Task Recommendation_SameNameBranches_AreRankedOncePerName()
    {
        // STEP-01: Get first from AgentTestData.Dish, set first.Name to a test text, get copy from AgentTestData.Dish, set copy.Name to a test text and 3 more.
        var first = AgentTestData.Dish(1); first.Name = "Phở Bò";
        var copy = AgentTestData.Dish(2); copy.Name = "phở  bò";
        var other = AgentTestData.Dish(3); other.Name = "Bún Chả";
        // STEP-02: Declare sent to capture a call argument; mock IRecommendationService; stub fixture.GetCandidateIdsAsync and reco.RecommendConversationAsync.
        var fixture = AgentTestData.Validator(first, copy, other); var reco = new Mock<IRecommendationService>();
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1, 2, 3 });
        IReadOnlyList<int>? sent = null;
        reco.Setup(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), It.IsAny<int>(), It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .Callback<int, IReadOnlyList<int>, int, RecommendationVector, CancellationToken>((_, ids, _, _, _) => sent = ids)
            .ReturnsAsync((int _, IReadOnlyList<int> ids, int _, RecommendationVector _, CancellationToken _) =>
                new RecommendationResult(Guid.NewGuid(), ids.ToArray(), [], ids.Select(_ => .9).ToArray(), "conversation"));
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { Preferences = new() { Initialized = true, BaselineVector = Enumerable.Repeat(.25, 12).ToArray() } };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Run tools.ExecuteAsync with context.
        await tools.ExecuteAsync(context, Recommend(new(Scope: "slot", DayNumber: 1, MealSlot: "lunch", TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [], TopK: 12)), default);

        // STEP-05: Assert sent equals 1.
        sent.Should().Equal(1, 3);
    }

    // TEST-21: Breakfast ranks only main dishes by default, while a drinks-only breakfast request or a side-item search with a category filter ranks drinks without adding the main-dish filter; covers default, drinks-only request and side search.
    /// <summary>Answers "if I want only drinks for breakfast, does the Agent push a main dish?": the user's category request always wins.</summary>
    [Theory]
    [InlineData("default")][InlineData("drinks-only")][InlineData("side-search")]
    public async Task Recommendation_BreakfastMainDefault_YieldsToExplicitCategory(string mode)
    {
        // STEP-01: Create drink as ANGI.Domain.Entities.Tag, create main as ANGI.Domain.Entities.Tag, get mainDish from AgentTestData.Dish and 4 more.
        var drink = new ANGI.Domain.Entities.Tag { Id = 4, Code = "local_sf_drink", Name = "Đồ uống", TagType = ANGI.Domain.Enums.TagType.Category };
        var main = new ANGI.Domain.Entities.Tag { Id = 5, Code = "local_sf_main", Name = "Món Chính", TagType = ANGI.Domain.Enums.TagType.Category };
        var mainDish = AgentTestData.Dish(1); mainDish.DishTags.Add(new() { DishId = 1, TagId = 5, Tag = main });
        var drinkDish = AgentTestData.Dish(2); drinkDish.DishTags.Add(new() { DishId = 2, TagId = 4, Tag = drink });
        // STEP-02: Declare filter to capture a call argument and 1 more; mock IRecommendationService; stub fixture.GetTagsAsync and 2 more.
        var fixture = AgentTestData.Validator(mainDish, drinkDish); var reco = new Mock<IRecommendationService>();
        fixture.Tags.Setup(x => x.GetTagsAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { drink, main });
        DishCandidateFilter? filter = null;
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback<DishCandidateFilter, int, CancellationToken>((value, _, _) => filter = value).ReturnsAsync(new[] { 1, 2 });
        IReadOnlyList<int>? sent = null;
        reco.Setup(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), It.IsAny<int>(), It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .Callback<int, IReadOnlyList<int>, int, RecommendationVector, CancellationToken>((_, ids, _, _, _) => sent = ids)
            .ReturnsAsync((int _, IReadOnlyList<int> ids, int _, RecommendationVector _, CancellationToken _) =>
                new RecommendationResult(Guid.NewGuid(), ids.ToArray(), [], ids.Select(_ => .9).ToArray(), "conversation"));
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["breakfast"],
            Preferences = new() { Initialized = true, BaselineVector = Enumerable.Repeat(.25, 12).ToArray() },
            MealRequests = mode == "drinks-only" ? [new(1, "breakfast", null, null, [4])] : [] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Run tools.ExecuteAsync with context.
        await tools.ExecuteAsync(context, Recommend(new(Scope: "slot", DayNumber: 1, MealSlot: "breakfast",
            TagIds: mode == "side-search" ? [4] : [], ExcludeTagIds: [], ExcludeDishIds: [], TopK: 12)), default);

        // STEP-05: Assert depending on mode == "default", filter.TagIds equals 5, sent equals 1, filter.TagIds equals 4, sent equals 2 and 1 more.
        if (mode == "default") { filter!.TagIds.Should().Equal((short)5); sent.Should().Equal(1); }
        else { filter!.TagIds.Should().Equal((short)4); sent.Should().Equal(2); }
        context.Grants.Single().Constraints.TagIds.Should().Equal(filter.TagIds);
    }

    // TEST-22: When a trip search is split (day-1 lunch has its own taste), a later part with the same ranking lists names the earlier
    // part already offered first at the end, and an earlier one-meal part only reserves its top dish rather than its whole list.
    /// <summary>Keeps days 2..N from receiving the exact candidates of day 1 when their ranking inputs are identical.</summary>
    [Fact]
    public async Task Recommendation_SplitScope_LaterPartsPreferNamesNotYetOffered()
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        // STEP-02: Mock IRecommendationService; stub fixture.GetCandidateIdsAsync and reco.RecommendConversationAsync.
        var fixture = AgentTestData.Validator(AgentTestData.Dish(1), AgentTestData.Dish(2), AgentTestData.Dish(3)); var reco = new Mock<IRecommendationService>();
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1, 2, 3 });
        reco.Setup(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), It.IsAny<int>(), It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, IReadOnlyList<int> ids, int _, RecommendationVector _, CancellationToken _) =>
                new RecommendationResult(Guid.NewGuid(), ids.ToArray(), [], ids.Select(_ => .9).ToArray(), "conversation"));
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { NumDays = 2, MealSlots = ["lunch"],
            Preferences = new() { Initialized = true, BaselineVector = Enumerable.Repeat(.25, 12).ToArray() },
            TasteOverrides = [new(1, "lunch", [new("spicy", 1)])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Run tools.ExecuteAsync with context.
        await tools.ExecuteAsync(context, Recommend(new(TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [], TopK: 12)), default);

        // STEP-05: Assert context.Grants.Single(filtered).DishIds equals 1 and context.Grants.Single(filtered).DishIds equals 2.
        context.Grants.Single(grant => grant.DayNumber == 1).DishIds.Should().Equal(1, 2, 3);
        context.Grants.Single(grant => grant.DayNumber == 2).DishIds.Should().Equal(2, 3, 1);
    }

    // TEST-23: Recommendation results carry the trip budget split over the planned meals as a price reference, and none without a budget; covers both.
    /// <summary>Gives the model a per-meal price reference so it can pick dishes that fit the trip budget.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task Recommendation_Result_IncludesBudgetPerMeal(bool budgeted)
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        // STEP-02: Mock IRecommendationService; stub fixture.GetCandidateIdsAsync and reco.RecommendConversationAsync.
        var fixture = AgentTestData.Validator(AgentTestData.Dish(1)); var reco = new Mock<IRecommendationService>();
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1 });
        reco.Setup(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), It.IsAny<int>(), It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecommendationResult(Guid.NewGuid(), [1], [], [.9], "conversation"));
        // STEP-03: Get context from AgentTestData.Context, set context.State to a copy of context.State and create tools as AgentToolExecutor.
        var context = AgentTestData.Context();
        context.State = context.State with { NumDays = 2, MealSlots = ["breakfast", "lunch"],
            BudgetAmount = budgeted ? 500000 : null, BudgetUnrestricted = !budgeted,
            Preferences = new() { Initialized = true, BaselineVector = Enumerable.Repeat(.25, 12).ToArray() } };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());

        // STEP-04: Run tools.ExecuteAsync with context and keep response.
        var response = await tools.ExecuteAsync(context, Recommend(new(Scope: "slot", DayNumber: 1, MealSlot: "lunch", TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [], TopK: 12)), default);

        // STEP-05: Get budgetPerMeal from response.GetProperty.
        var budgetPerMeal = response.GetProperty("budgetPerMeal");
        // STEP-06: Assert depending on budgeted, budgetPerMeal is 125000 and budgetPerMeal.ValueKind is JsonValueKind.Null.
        if (budgeted) budgetPerMeal.GetDecimal().Should().Be(125000);
        else budgetPerMeal.ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>Builds the conversation-16 state: two days of declared meals, a real snack tag and snack requests for day-1 lunch and dinner.</summary>
    private static (AgentToolExecutor Tools, AgentProposalValidator Validator, Mock<IRecommendationService> Reco, AgentContext Context)
        SnackFixture(Func<DishCandidateFilter, int[]> candidatesFor)
    {
        var snack = new ANGI.Domain.Entities.Tag { Id = 6, TagType = ANGI.Domain.Enums.TagType.Category, Code = "local_sf_snack", Name = "Món ăn vặt" };
        var snackDish = AgentTestData.Dish(2);
        snackDish.DishTags.Add(new() { DishId = 2, TagId = 6, Tag = snack });
        var fixture = AgentTestData.Validator(AgentTestData.Dish(), snackDish);
        fixture.Tags.Setup(x => x.GetTagsAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { snack });
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DishCandidateFilter filter, int _, CancellationToken _) => candidatesFor(filter));
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), It.IsAny<int>(), It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, IReadOnlyList<int> ids, int _, RecommendationVector _, CancellationToken _) =>
                new RecommendationResult(Guid.NewGuid(), ids.ToArray(), [], ids.Select(_ => .9).ToArray(), "conversation"));
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["breakfast", "lunch", "dinner"],
            Preferences = new() { Initialized = true, BaselineVector = Enumerable.Repeat(.25, 12).ToArray() },
            TasteTargets = [new("sour", .25)], MealRequests = [new(1, "lunch", null, null, [6]), new(1, "dinner", null, null, [6])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        return (tools, fixture.Validator, reco, context);
    }

    /// <summary>Builds a get_recommendation tool call from the given input, serialized with the agent JSON options.</summary>
    private static AgentToolCall Recommend(AgentRecommendationInput input) => new("rank", "get_recommendation", JsonSerializer.SerializeToElement(input, AgentJson.Options));

    // TEST-24: Dropped dishes are remembered once, a dish back in the plan is forgotten, the list is bounded and an empty list stays null.
    /// <summary>Stops repeated "món khác" from cycling back without growing the LLM context without limit.</summary>
    [Fact]
    public void Replaced_TracksDroppedNamesWithinBound()
    {
        // STEP-01: Derive a from a copy of AgentTestData.Item(), derive b from a copy of AgentTestData.Item(), get source from AgentTestData.Snapshot and 1 more.
        var a = AgentTestData.Item(1, "a") with { DishName = "Phở" };
        var b = AgentTestData.Item(2, "b") with { DishName = "Bún" };
        var source = AgentTestData.Snapshot(a, b);
        var state = AgentTestData.State() with { ReplacedDishNames = ["Cơm", "Bún"] };

        // STEP-02: Assert AgentScopedPlanning.Replaced() equals the expected text and AgentScopedPlanning.Replaced() is null.
        AgentScopedPlanning.Replaced(state, source, AgentTestData.Snapshot(b)).Should().Equal("Cơm", "Phở");
        AgentScopedPlanning.Replaced(AgentTestData.State(), source, source).Should().BeNull();
        AgentScopedPlanning.Replaced(AgentTestData.State(), null, source).Should().BeNull();
        // STEP-03: Derive many from a copy of AgentTestData.State().
        var many = AgentTestData.State() with { ReplacedDishNames = Enumerable.Range(0, 60).Select(index => $"Món {index}").ToArray() };
        // STEP-04: Assert AgentScopedPlanning.Replaced() has 40 item(s).
        AgentScopedPlanning.Replaced(many, null, source).Should().HaveCount(40).And.EndWith("Món 59");
    }
}
