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
        result.Snapshot.Should().BeNull(); result.Reply.Should().NotBeEmpty().And.NotContain("Please clarify");
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Repair one invalid schema response and reject a second invalid response.
    /// <summary>Checks both successful repair and the single-repair ceiling.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task RunAsync_SchemaRepair_IsBounded(bool recover)
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.InvalidSchema));
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(recover ? new AgentLlmStep([], AgentTestData.Answer()) : AgentLlmStep.Failed(AgentLlmFailureKind.InvalidSchema));
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

    // TEST-08: A model may request draft controls but cannot use those actions to also mutate state or items.
    /// <summary>Checks the chat intent allowlist and keeps backend lifecycle effects separate from proposal deltas.</summary>
    [Theory]
    [InlineData("discard_proposal", "discard")]
    [InlineData("reset_conversation", "reset")]
    public async Task RunAsync_DraftControl_ReturnsBackendAction(string action, string expected)
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(service => service.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = action, Reply = "Draft control" }));
        var result = await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        result.Action.Should().Be(expected); result.Snapshot.Should().BeNull(); result.Commit.Should().BeNull();
    }

    // TEST-09: A model cannot authorize its own relaxed constraints, even when it sets its consent flag.
    /// <summary>Turns relaxation into a persisted confirmation question rather than a provider error or automatic change.</summary>
    [Fact]
    public async Task RunAsync_RelaxedState_StagesHumanConsent()
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(service => service.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = "Already confirmed",
                ConstraintsChangeConfirmed = true, State = AgentTestData.State() with { BudgetAmount = 700000 } }));
        var result = await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        result.State.BudgetAmount.Should().Be(500000); result.State.PendingStateChange!.BudgetAmount.Should().Be(700000);
        result.Snapshot.Should().BeNull(); result.Commit.Should().BeNull();
    }

    // TEST-10: Non-schema failures never enter repair, including quota, transport, refusal and truncated output.
    /// <summary>Protects free provider quota while retaining a reason-specific API failure.</summary>
    [Theory]
    [InlineData(AgentLlmFailureKind.Configuration)][InlineData(AgentLlmFailureKind.Authentication)]
    [InlineData(AgentLlmFailureKind.Timeout)][InlineData(AgentLlmFailureKind.TurnTimeout)]
    [InlineData(AgentLlmFailureKind.Network)][InlineData(AgentLlmFailureKind.RateLimited)]
    [InlineData(AgentLlmFailureKind.RejectedRequest)][InlineData(AgentLlmFailureKind.ProviderUnavailable)]
    [InlineData(AgentLlmFailureKind.InvalidResponse)][InlineData(AgentLlmFailureKind.Refused)][InlineData(AgentLlmFailureKind.Truncated)]
    public async Task RunAsync_ProviderFailure_DoesNotRepair(AgentLlmFailureKind kind)
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentLlmStep.Failed(kind, 429, 45));
        var act = () => Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        var error = (await act.Should().ThrowAsync<AgentProviderException>()).Which;
        error.ErrorCode.Should().Be("AGENT_UNAVAILABLE"); error.Failure.Should().Be(new AgentLlmFailure(kind, 429, 45));
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()), Times.Once);
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-11: An unclassified missing response cannot be assumed to contain repairable model output.
    /// <summary>Fails closed without spending another provider request on an unknown failure.</summary>
    [Fact]
    public async Task RunAsync_NullResult_DoesNotRepair()
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync((AgentLlmStep?)null);
        var act = () => Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        (await act.Should().ThrowAsync<AgentProviderException>()).Which.Failure.Kind.Should().Be(AgentLlmFailureKind.InvalidResponse);
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_Transient503_RetriesWithoutRepeatingTools()
    {
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.ProviderUnavailable, 503))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer("Recovered")));
        (await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)).Reply.Should().Contain("catalog ANGI").And.NotContain("Recovered");
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RunAsync_Repeated503_StopsAfterTwoRetries()
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.ProviderUnavailable, 503));
        await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)))
            .Should().ThrowAsync<AgentProviderException>();
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Theory]
    [InlineData("rate_limit", true)]
    [InlineData("daily_quota_exceeded", false)]
    public async Task RunAsync_Gemini429_RetriesOnlyShortTemporaryLimits(string reason, bool recover)
    {
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], null, Failure: new(AgentLlmFailureKind.RateLimited, 429, 1, "RESOURCE_EXHAUSTED", reason)))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer("Recovered")));
        if (recover) (await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)).Reply.Should().Contain("catalog ANGI").And.NotContain("Recovered");
        else await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None))).Should().ThrowAsync<AgentProviderException>();
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()), Times.Exactly(recover ? 2 : 1));
    }

    [Fact]
    public async Task RunAsync_CreateOverExistingProposal_RepairsAsPatch()
    {
        var llm = new Mock<IAgentLlmService>();
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Wrong action", Items = [AgentTestData.Item()] }));
        llm.Setup(x => x.RunAsync(context, true, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "patch_proposal", Reply = "Moved", Changes = [new("move", "a", AgentTestData.Item() with { DayNumber = 2 })] }));
        var result = await Create(llm.Object).RunAsync(context, CancellationToken.None);
        result.Snapshot!.Items.Single().DayNumber.Should().Be(2);
        context.RepairFeedback.Should().Contain("phạm vi");
    }

    // TEST-12: Caller cancellation during a provider call propagates without repair or an availability fallback.
    /// <summary>Keeps the UI Stop action distinct from server or provider timeouts.</summary>
    [Fact]
    public async Task RunAsync_CallerCancellationDuringProvider_Propagates()
    {
        var llm = new Mock<IAgentLlmService>(); using var cancellation = new CancellationTokenSource();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (AgentContext _, bool _, CancellationToken ct) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, ct); return (AgentLlmStep?)null; });
        var act = () => Create(llm.Object).RunAsync(AgentTestData.Context(), cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-13: Failure telemetry includes safe diagnostic categories and timing without leaking prompt content.
    /// <summary>Makes future provider failures distinguishable without logging raw requests or upstream bodies.</summary>
    [Fact]
    public async Task RunAsync_ProviderFailure_RecordsSafeDiagnostics()
    {
        var llm = new Mock<IAgentLlmService>(); var telemetry = new Mock<IAgentTelemetry>(); object? captured = null;
        telemetry.Setup(t => t.Record("llm", It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<object>()))
            .Callback<string, long, long?, long?, object?>((_, _, _, _, data) => captured = data);
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.RateLimited, 429, 45));
        var context = AgentTestData.Context(); context.Request.Content = "private-prompt-must-not-leak";
        var act = () => Create(llm.Object, telemetry: telemetry.Object).RunAsync(context, CancellationToken.None);
        await act.Should().ThrowAsync<AgentProviderException>();
        using var diagnostic = System.Text.Json.JsonDocument.Parse(AgentJson.Write(captured));
        diagnostic.RootElement.GetProperty("failureKind").GetString().Should().Be("RateLimited");
        diagnostic.RootElement.GetProperty("httpStatus").GetInt32().Should().Be(429);
        diagnostic.RootElement.GetProperty("retryAfterSeconds").GetInt32().Should().Be(45);
        diagnostic.RootElement.GetProperty("callNumber").GetInt32().Should().Be(1);
        diagnostic.RootElement.GetProperty("repair").GetBoolean().Should().BeFalse();
        diagnostic.RootElement.GetProperty("elapsedMs").GetDouble().Should().BeGreaterThanOrEqualTo(0);
        AgentJson.Write(captured).Should().NotContain("private-prompt");
    }

    /// <summary>Builds orchestration from deterministic catalog boundaries and observable provider mocks.</summary>
    private static AgentOrchestrator Create(IAgentLlmService llm, AgentLimits? configuredLimits = null, IAgentTelemetry? telemetry = null)
    {
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        telemetry ??= Mock.Of<IAgentTelemetry>(); var limits = configuredLimits ?? new AgentLimits(); var state = new AgentStatePolicy();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), state,
            new AgentRouteEstimator(fixture.Routes.Object), limits, telemetry);
        return new(llm, tools, state, new(), fixture.Validator, limits, telemetry);
    }
}
