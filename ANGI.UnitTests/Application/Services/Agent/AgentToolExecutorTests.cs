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
    // TEST-01: Exclude previous scoped dishes before ranking alternatives and preserve fallback provenance.
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

    // TEST-02: Reject unknown tools, injected user identity and malformed numeric or scope arguments before ranking.
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

    // TEST-03: Reject out-of-candidate IDs, malformed scores and invalid request IDs in successful ranking responses.
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

    // TEST-04: Empty candidate sets ask for clarification without calling ranking or loosening constraints.
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

    // TEST-05: Reject routing arbitrary dish IDs and reading a roadmap not bound to the conversation.
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

    /// <summary>Returns valid strict recommendation arguments with explicit empty lists.</summary>
    private static AgentRecommendationInput Input() => new(null, "Da Nang", "full", null, null, [], [], []);
    /// <summary>Serializes a complete strict tool call using the public camel-case contract.</summary>
    private static AgentToolCall Call(AgentRecommendationInput input) => new("c", "get_recommendation", JsonSerializer.SerializeToElement(input, AgentJson.Options));
}
