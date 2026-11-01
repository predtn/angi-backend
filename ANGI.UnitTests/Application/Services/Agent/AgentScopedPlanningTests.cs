using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.Services.Agent;
using ANGI.Application.UseCases.Validators.Agent;
using ANGI.UnitTests.Application.UseCases.Agent;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Checks scoped inheritance, named selectors, provenance and grounded replies at business boundaries.</summary>
public sealed class AgentScopedPlanningTests
{
    // TEST-01: A meal-level taste override wins over the day override without leaking into other days or the stored profile.
    /// <summary>Ensures scoped taste overrides apply only where the user asked for them.</summary>
    [Fact]
    public void Vector_MealOverridesDayWithoutLeakingToOtherDaysOrProfile()
    {
        var baseline = Enumerable.Repeat(.25d, 12).ToArray();
        var state = AgentTestData.State() with { Preferences = new() { Initialized = true, BaselineVector = baseline },
            TasteTargets = [new("sweet", .5)], TasteOverrides = [new(1, null, [new("spicy", 1)]), new(1, "dinner", [new("spicy", 0)])] };
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
        var state = AgentTestData.State() with { NumDays = 3,
            TasteOverrides = [new(1, null, [new("spicy", 1)])],
            MealRequests = [new(2, null, null, null, [4]), new(3, null, null, null, [], true)] };
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
        new AgentStateValidator().Validate(state).IsValid.Should().BeFalse();
    }

    // TEST-04: Legacy state JSON without scoped fields reads with empty overrides, requests and meal slots and the default replan mode.
    /// <summary>Keeps conversations stored before scoped planning existed readable with safe defaults.</summary>
    [Fact]
    public void LegacyJson_ReadsWithNoScopedOverridesOrCoverage()
    {
        var state = AgentJson.Read<AgentState>("{\"province\":\"Da Nang\",\"numDays\":2,\"budgetAmount\":500000}");
        state.TasteOverrides.Should().BeEmpty(); state.MealRequests.Should().BeEmpty(); state.MealSlots.Should().BeEmpty();
        state.PreferenceApplication.Should().Be("replan");
    }

    // TEST-05: A broad recommendation while days have different taste overrides returns scoped_requests_required without calling reco or the dish catalog.
    /// <summary>Prevents one trip-wide ranking from being used when days need different taste vectors.</summary>
    [Fact]
    public async Task BroadRecommendation_DifferentDaysRequireScopedCalls()
    {
        var fixture = AgentTestData.Validator(); var reco = new Mock<IRecommendationService>();
        var context = AgentTestData.Context();
        context.State = context.State with { TasteOverrides = [new(1, null, [new("spicy", 1)])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        var response = await tools.ExecuteAsync(context, Recommend(new(TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [])), default);
        response.GetProperty("status").GetString().Should().Be("scoped_requests_required");
        context.Grants.Should().BeEmpty(); reco.VerifyNoOtherCalls(); fixture.Dishes.VerifyNoOtherCalls();
    }

    // TEST-06: A day-scoped recommendation sends the effective override vector to reco without changing the trip-wide default vector.
    /// <summary>Ensures a scoped ranking uses its override vector while the trip default stays untouched.</summary>
    [Fact]
    public async Task ScopedRecommendation_SendsEffectiveVectorWithoutChangingTripDefault()
    {
        var fixture = AgentTestData.Validator(AgentTestData.Dish()); var reco = new Mock<IRecommendationService>();
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1 });
        RecommendationVector? captured = null;
        reco.Setup(x => x.RecommendConversationAsync(3, It.IsAny<IReadOnlyList<int>>(), 12, It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .Callback<int, IReadOnlyList<int>, int?, RecommendationVector, CancellationToken>((_, _, _, vector, _) => captured = vector)
            .ReturnsAsync(new RecommendationResult(Guid.NewGuid(), [1], [], [.9], "conversation"));
        var context = AgentTestData.Context();
        context.State = context.State with { Preferences = new() { Initialized = true, BaselineVector = Enumerable.Repeat(.25, 12).ToArray() },
            TasteOverrides = [new(1, null, [new("spicy", 1)])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>());
        var result = await tools.ExecuteAsync(context, Recommend(new(Scope: "day", DayNumber: 1, TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [], TopK: 12)), default);
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
        var fixture = AgentTestData.Validator(); var reco = new Mock<IRecommendationService>(); var catalog = new Mock<IAgentCatalogRepository>();
        var match = new AgentCatalogMatch(11, "Quán A", 11, "Quán A", "Da Nang", "Đường A", outcome != "unavailable");
        catalog.Setup(x => x.SearchAsync("restaurant", "Quán A", "Da Nang", 21, It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome switch { "not_found" or "outside_province" => [], "ambiguous" => [match, match with { Id = 12, RestaurantId = 12 }], _ => [match] });
        catalog.Setup(x => x.SearchAsync("restaurant", "Quán A", null, 21, It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome == "outside_province" ? [match with { Province = "Ha Noi" }] : []);
        var context = AgentTestData.Context(); context.State = context.State with { MealRequests = [new(null, null, null, "Quán A", [])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, reco.Object, new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);
        var result = await tools.ExecuteAsync(context, Recommend(new(TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [])), default);
        result.GetProperty("status").GetString().Should().Be(outcome);
        context.Grants.Should().BeEmpty(); reco.VerifyNoOtherCalls(); fixture.Dishes.VerifyNoOtherCalls();
    }

    // TEST-08: A named dish request restricts candidate ids and the restaurant before ranking.
    /// <summary>Ensures a named dish request only ranks the matching dish at the matching restaurant.</summary>
    [Fact]
    public async Task NamedDish_QueryRestrictsIdsAndRestaurantBeforeRanking()
    {
        var fixture = AgentTestData.Validator(); var catalog = new Mock<IAgentCatalogRepository>();
        catalog.Setup(x => x.SearchAsync("restaurant", "Quán A", "Da Nang", 21, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new(11, "Quán A", 11, "Quán A", "Da Nang", "A", true)]);
        catalog.Setup(x => x.SearchAsync("dish", "Món X", "Da Nang", 21, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new(7, "Món X", 11, "Quán A", "Da Nang", "A", true), new(8, "Món X", 12, "Quán B", "Da Nang", "B", true)]);
        DishCandidateFilter? filter = null;
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<DishCandidateFilter>(), 2000, It.IsAny<CancellationToken>()))
            .Callback<DishCandidateFilter, int, CancellationToken>((value, _, _) => filter = value).ReturnsAsync(Array.Empty<int>());
        var context = AgentTestData.Context(); context.State = context.State with { MealRequests = [new(null, null, "Món X", "Quán A", [])] };
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), new(), new(fixture.Routes.Object), new(), Mock.Of<IAgentTelemetry>(), catalogSearch: catalog.Object);
        await tools.ExecuteAsync(context, Recommend(new(TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [])), default);
        filter!.RestaurantId.Should().Be(11); filter.DishIds.Should().Equal(7);
    }

    // TEST-09: A retained dish whose taste scope changed needs a new grant, and the new grant updates the item's request identity.
    /// <summary>Ensures a kept dish is re-authorized under the new taste and attributed to the new reco request.</summary>
    [Fact]
    public async Task RetainedDish_ChangedTasteRequiresNewGrantAndUpdatesRequestIdentity()
    {
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        var original = AgentTestData.Snapshot(AgentTestData.Item() with { RequestId = Guid.NewGuid() });
        var state = original.State with { TasteOverrides = [new(1, null, [new("spicy", 1)])] };
        var snapshot = original with { State = state };
        (await fixture.Validator.ValidateAsync(snapshot, original, false, false, default, [])).Validation.Errors.Should().NotBeEmpty();
        var grant = AgentTestData.Grant([1], 1, "lunch") with { Constraints = state };
        var ranked = await fixture.Validator.ValidateAsync(snapshot with { Grants = [grant] }, original, false, false, default, [grant]);
        ranked.Validation.Errors.Should().BeEmpty(); ranked.Snapshot.Items[0].RequestId.Should().Be(grant.RequestId);
        ranked.Snapshot.Items[0].RankingFingerprint.Should().NotBeNull();
    }

    // TEST-10: A meal not affected by the change, or a future-only taste change, does not require reranking; covers both cases.
    /// <summary>Avoids needless reranking for meals that a taste change does not affect.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task UnaffectedOrFutureOnlyMeal_DoesNotRequireReranking(bool futureOnly)
    {
        var source = AgentTestData.Snapshot(AgentTestData.Item(day: futureOnly ? (short)1 : (short)2));
        var changed = source with { State = source.State with { PreferenceApplication = futureOnly ? "future" : "replan",
            TasteOverrides = [new(1, null, [new("spicy", 1)])] } };
        var result = await AgentTestData.Validator(AgentTestData.Dish()).Validator.ValidateAsync(changed, source, false, false, default, []);
        result.Validation.Errors.Should().BeEmpty();
    }

    // TEST-11: Declared meal coverage rejects a plan missing its breakfast and dinner slots.
    /// <summary>Ensures a plan covers every meal slot the user declared.</summary>
    [Fact]
    public async Task DeclaredMealCoverage_RejectsMissingBreakfastAndDinner()
    {
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        snapshot = snapshot with { State = snapshot.State with { NumDays = 1, MealSlots = ["breakfast", "lunch", "dinner"] } };
        var result = await AgentTestData.Validator(AgentTestData.Dish()).Validator.ValidateAsync(snapshot, null, false, false, default);
        result.Validation.Errors.Should().Contain(x => x.Contains("breakfast")).And.Contain(x => x.Contains("dinner"));
    }

    // TEST-12: A named meal request rejects another dish even when that dish has a candidate grant.
    /// <summary>Ensures a candidate grant cannot override an explicit dish or restaurant request.</summary>
    [Fact]
    public async Task NamedRequest_ValidatorRejectsAnotherDishEvenWithCandidateGrant()
    {
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        var state = snapshot.State with { MealRequests = [new(1, "lunch", "Món được yêu cầu", null, [])] };
        snapshot = snapshot with { State = state, Grants = [AgentTestData.Grant() with { Constraints = state }] };
        var result = await AgentTestData.Validator(AgentTestData.Dish()).Validator.ValidateAsync(snapshot, null, false, false, default);
        result.Validation.Errors.Should().Contain(x => x.Contains("món/quán"));
    }

    // TEST-13: A novelty request rejects an initial plan that repeats a dish in another meal.
    /// <summary>Ensures a novelty request is honoured by rejecting repeated dishes.</summary>
    [Fact]
    public async Task Novelty_InitialPlanCannotRepeatTheDishInAnotherMeal()
    {
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item(), AgentTestData.Item(key: "b", day: 2));
        var state = snapshot.State with { MealRequests = [new(2, null, null, null, [], true)] };
        snapshot = snapshot with { State = state, Grants = [AgentTestData.Grant() with { Constraints = state }] };
        var result = await AgentTestData.Validator(AgentTestData.Dish()).Validator.ValidateAsync(snapshot, null, false, false, default);
        result.Validation.Errors.Should().Contain(x => x.Contains("bị lặp"));
    }

    // TEST-14: Confirming a re-evaluated retained dish keeps the new request id and rejects a tampered ranking fingerprint.
    /// <summary>Ensures confirmation keeps the re-evaluated attribution and detects a forged ranking fingerprint.</summary>
    [Fact]
    public async Task Confirm_ReevaluatedRetainedDishPreservesNewRequestAndDetectsTampering()
    {
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        var source = AgentTestData.Snapshot(AgentTestData.Item() with { Price = 50000, SourceItemId = 8, RequestId = Guid.NewGuid() });
        var state = source.State with { TasteOverrides = [new(1, null, [new("spicy", 1)])] };
        var grant = AgentTestData.Grant([1], 1, "lunch") with { Constraints = state };
        var draft = source with { State = state, Grants = [grant], RetainedItems = source.Items };
        var reviewed = await fixture.Validator.ValidateAsync(draft, source, false, false, default, [grant]);
        reviewed.Validation.Errors.Should().BeEmpty();
        var confirmed = await fixture.Validator.ValidateAsync(reviewed.Snapshot, source, true, false, default);
        confirmed.Validation.Errors.Should().BeEmpty(); confirmed.Snapshot.Items[0].RequestId.Should().Be(grant.RequestId);
        var tampered = reviewed.Snapshot with { Items = [reviewed.Snapshot.Items[0] with { RankingFingerprint = "forged" }] };
        (await fixture.Validator.ValidateAsync(tampered, source, true, false, default)).Validation.Errors.Should().NotBeEmpty();
    }

    // TEST-15: A future-only taste answer creates a savable revision that keeps the current dishes without reranking.
    /// <summary>Ensures a taste change for future suggestions can be saved without rebuilding the current plan.</summary>
    [Fact]
    public async Task FutureTasteOnlyAnswer_CreatesSavableRevisionWithoutReranking()
    {
        var fixture = new AgentUseCaseFixture();
        var source = AgentTestData.Snapshot(AgentTestData.Item() with { Price = 50000 });
        var context = AgentTestData.Context(source);
        fixture.Llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer() with { State = context.State with {
                PreferenceApplication = "future", TasteOverrides = [new(1, null, [new("spicy", 1)])] } }));
        var result = await fixture.Orchestrator().RunAsync(context, default);
        result.Snapshot.Should().NotBeNull(); result.Snapshot!.Items[0].DishId.Should().Be(source.Items[0].DishId);
        result.Snapshot.State.PreferenceApplication.Should().Be("future"); result.State.PendingQuestion.Should().BeNull();
        context.Grants.Should().BeEmpty(); result.Reply.Should().Contain("gợi ý tiếp theo");
    }

    // TEST-16: A taste-update answer cannot claim the old proposal was updated when no new proposal was built.
    /// <summary>Prevents the reply from telling the user a plan changed when it did not.</summary>
    [Fact]
    public async Task TasteUpdateAnswer_CannotClaimOldProposalWasUpdated()
    {
        var fixture = new AgentUseCaseFixture(); var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        fixture.Llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer("Đã cập nhật tất cả") with {
                State = context.State with { TasteOverrides = [new(1, null, [new("spicy", 1)])] } }));
        var result = await fixture.Orchestrator().RunAsync(context, default);
        result.Snapshot.Should().BeNull(); result.State.PendingQuestion.Should().NotBeNull();
        result.Reply.Should().Contain("chưa có đề xuất cập nhật").And.NotContain("Đã cập nhật tất cả");
        fixture.Llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-17: Provider prose cannot invent user-visible catalog facts; covers answer and clarify actions.
    /// <summary>Prevents unverified model prose from showing invented restaurant facts to the user.</summary>
    [Theory]
    [InlineData("answer")][InlineData("clarify")]
    public async Task ProviderProse_CannotInventUserVisibleCatalogFacts(string action)
    {
        var fixture = new AgentUseCaseFixture();
        fixture.Llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new() { Action = action, Reply = "Nhà hàng Bịa mở 24/24, miễn phí và chắc chắn không cay.", Clarification = action == "clarify" ? "taste_scope" : null }));
        var result = await fixture.Orchestrator().RunAsync(AgentTestData.Context(), default);
        result.Reply.Should().NotContain("Bịa").And.NotContain("24/24").And.NotContain("miễn phí");
        if (action == "clarify") result.Reply.Should().Contain("toàn chuyến").And.Contain("ngày/bữa");
    }

    /// <summary>Builds a get_recommendation tool call from the given input, serialized with the agent JSON options.</summary>
    private static AgentToolCall Recommend(AgentRecommendationInput input) => new("rank", "get_recommendation", JsonSerializer.SerializeToElement(input, AgentJson.Options));
}
