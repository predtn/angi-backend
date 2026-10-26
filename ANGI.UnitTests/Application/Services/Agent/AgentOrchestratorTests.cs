using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies bounded LLM execution and proposal preservation on all controlled failure paths.</summary>
public sealed class AgentOrchestratorTests
{
    // TEST-01: Explanation and clarification use existing context without calling ranking or routes.
    /// <summary>Checks the no-reranking path for ordinary conversational answers.</summary>
    [Theory]
    [InlineData("answer")][InlineData("clarify")]
    public async Task RunAsync_Answer_ReturnsWithoutTools(string action)
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer() with { Action = action }));
        var result = await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        result.Snapshot.Should().BeNull(); result.Reply.Should().Be("Please clarify");
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Repair one invalid schema response and reject a second invalid response.
    /// <summary>Checks both successful repair and the single-repair ceiling.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task RunAsync_SchemaRepair_IsBounded(bool recover)
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync((AgentLlmStep?)null);
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(recover ? new AgentLlmStep([], AgentTestData.Answer()) : null);
        if (recover) (await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)).Snapshot.Should().BeNull();
        else await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-03: A hallucinated dish never produces a new ready proposal or overwrites the source snapshot.
    /// <summary>Verifies deterministic validation after a structurally valid model output.</summary>
    [Fact]
    public async Task RunAsync_HallucinatedDish_ReturnsBlockingValidation()
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Preview", Items = [AgentTestData.Item(999)] }));
        var result = await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        result.Snapshot.Should().BeNull(); result.Validation!.Errors.Should().NotBeEmpty();
    }

    // TEST-04: Context size and cancellation prevent uncontrolled provider work.
    /// <summary>Checks resource caps before an LLM request and preserves caller cancellation.</summary>
    [Fact]
    public async Task RunAsync_OversizedContextAndCancellation_StopBeforeProvider()
    {
        var llm = new Mock<IAgentLlmService>();
        var context = AgentTestData.Context(); context.Request.Content = new('x', 130000);
        var act = () => Create(llm.Object).RunAsync(context, CancellationToken.None);
        await act.Should().ThrowAsync<ServiceUnavailableException>();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), cancellation.Token))).Should().ThrowAsync<OperationCanceledException>();
        llm.VerifyNoOtherCalls();
    }

    // TEST-05: The total backend timeout cancels a slow provider and reports a controlled unavailable result.
    /// <summary>Verifies processing is bounded independently of provider HTTP timeouts.</summary>
    [Fact]
    public async Task RunAsync_TotalTimeout_CancelsSlowProvider()
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (AgentContext _, bool _, CancellationToken ct) => { await Task.Delay(Timeout.Infinite, ct); return (AgentLlmStep?)null; });
        var act = () => Create(llm.Object, new AgentLimits { TimeoutSeconds = 1 }).RunAsync(AgentTestData.Context(), CancellationToken.None);
        (await act.Should().ThrowAsync<ServiceUnavailableException>()).Which.ErrorCode.Should().Be("AGENT_UNAVAILABLE");
    }

    // TEST-06: Repeated tools stop at the round, aggregate-call and total-LLM-call caps.
    /// <summary>Verifies backend execution limits against a provider that never returns a final answer.</summary>
    [Theory]
    [InlineData("rounds", 2)][InlineData("calls", 1)][InlineData("llm", 2)]
    public async Task RunAsync_RepeatedTools_EnforcesEveryCallCap(string limit, int expectedCalls)
    {
        var limits = new AgentLimits { MaxToolRounds = limit == "rounds" ? 1 : 10,
            MaxToolCalls = limit == "calls" ? 1 : 20, MaxLlmCalls = limit == "llm" ? 2 : 5 };
        var llm = new Mock<IAgentLlmService>();
        var tool = new AgentToolCall("a", "get_roadmap", System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 88 }));
        var tools = limit == "calls" ? new[] { tool, tool with { Id = "b" } } : [tool];
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep(tools, null));
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = AgentTestData.State(), Request = new() { Content = "Explain" },
            Roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88 } };
        await ((Func<Task>)(async () => await Create(llm.Object, limits).RunAsync(context, CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(expectedCalls));
    }

    // TEST-07: Structurally valid model state with invalid business bounds reports an Agent failure instead of blaming user input.
    /// <summary>Verifies invalid model-enriched state cannot escape as user validation or create ready state.</summary>
    [Fact]
    public async Task RunAsync_InvalidModelState_ProducesControlledUnavailable()
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            AgentTestData.Answer() with { State = AgentTestData.State() with { NumDays = 0 } }));
        await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();
    }

    /// <summary>Builds orchestration from deterministic catalog boundaries and observable provider mocks.</summary>
    private static AgentOrchestrator Create(IAgentLlmService llm, AgentLimits? configuredLimits = null)
    {
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        var telemetry = Mock.Of<IAgentTelemetry>(); var limits = configuredLimits ?? new AgentLimits(); var state = new AgentStatePolicy();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), state,
            new AgentRouteEstimator(fixture.Routes.Object), limits, telemetry);
        return new(llm, tools, state, new(), fixture.Validator, limits, telemetry);
    }
}
