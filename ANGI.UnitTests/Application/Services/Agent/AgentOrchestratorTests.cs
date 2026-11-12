using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using ANGI.Application.Services.Agent.Constraints;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Intake;
using ANGI.Application.Services.Agent.Places;
using ANGI.Application.Services.Agent.Proposals;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies bounded LLM execution and proposal preservation on all controlled failure paths.</summary>
public sealed class AgentOrchestratorTests
{

    // TEST-01: An identical tool request in the same turn returns a duplicate notice, is not executed again and closes tools for the final call.
    /// <summary>A repeated identical tool request is answered from the turn, not re-executed, and closes tools for the final call.</summary>
    [Fact]
    public async Task RunAsync_RepeatedIdenticalTool_IsNotReexecutedAndClosesTools()
    {
        // STEP-01: Mock IAgentLlmService.
        var llm = new Mock<IAgentLlmService>();
        // STEP-02: Get arguments from System.Text.Json.JsonSerializer.SerializeToElement and create context as AgentContext.
        var arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 88 });
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = AgentTestData.State(), Request = new() { Content = "Explain" },
            Roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88 } };
        // STEP-03: Stub llm.RunAsync.
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([new("a", "get_roadmap", arguments)], null))
            .ReturnsAsync(new AgentLlmStep([new("b", "get_roadmap", arguments)], null))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-04: Call Create().RunAsync.
        await Create(llm.Object).RunAsync(context, CancellationToken.None);

        // STEP-05: Assert context.ToolsClosed is true, context.Exchanges has 2 item(s) and context.Exchanges[1].Results.Single().status is "duplicate_request".
        context.ToolsClosed.Should().BeTrue();
        context.Exchanges.Should().HaveCount(2);
        context.Exchanges[1].Results.Single().GetProperty("status").GetString().Should().Be("duplicate_request");
        // STEP-06: Verify RunAsync is called 3 times.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // TEST-02: The invalid-output notice and schema path are sent only on the call after a schema failure, not after a successful tool round.
    /// <summary>The invalid-output notice applies to the call right after a schema failure, not to every later tool round.</summary>
    [Fact]
    public async Task RunAsync_SchemaRepairThenTool_StopsSendingRepairNotice()
    {
        // STEP-01: Create context as AgentContext; mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = AgentTestData.State(), Request = new() { Content = "Explain" },
            Roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88 } };
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], null, Failure: new(AgentLlmFailureKind.InvalidSchema, 200, Reason: "output:$.state.preferences(unexpected)")))
            .ReturnsAsync(new AgentLlmStep([new("a", "get_roadmap", System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 88 }))], null))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-02: Call Create().RunAsync.
        await Create(llm.Object).RunAsync(context, CancellationToken.None);

        // STEP-03: Assert llm.Invocations.Select(filtered) equals false and context.RepairFeedback contains "$.state.preferences".
        llm.Invocations.Select(call => call.Arguments[1] is true).Should().Equal(false, true, false);
        context.RepairFeedback.Should().Contain("$.state.preferences");
    }

    // TEST-03: After tools are closed by a duplicate request, a further tool request fails the turn without executing it.
    /// <summary>Enforces the closed-tools state even if a provider ignores tool_choice=none.</summary>
    [Fact]
    public async Task RunAsync_ToolRequestAfterToolsClosed_FailsWithoutExecuting()
    {
        // STEP-01: Mock IAgentLlmService.
        var llm = new Mock<IAgentLlmService>();
        // STEP-02: Create context as AgentContext and get same from System.Text.Json.JsonSerializer.SerializeToElement.
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = AgentTestData.State(), Request = new() { Content = "Explain" },
            Roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88 } };
        var same = System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 88 });
        // STEP-03: Stub llm.RunAsync.
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([new("a", "get_roadmap", same)], null))
            .ReturnsAsync(new AgentLlmStep([new("b", "get_roadmap", same)], null))
            .ReturnsAsync(new AgentLlmStep([new("c", "get_roadmap", System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 89 }))], null));

        // STEP-04: Assert Create().RunAsync() throws ServiceUnavailableException and context.Exchanges has 2 item(s).
        await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(context, CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();

        context.Exchanges.Should().HaveCount(2);
        // STEP-05: Verify RunAsync is called 3 times.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // TEST-04: Two identical calls in one provider step execute once; the second gets the duplicate notice and tools close.
    /// <summary>Covers duplicates inside a single step, not only across rounds.</summary>
    [Fact]
    public async Task RunAsync_IdenticalCallsInOneStep_ExecuteOnce()
    {
        // STEP-01: Mock IAgentLlmService.
        var llm = new Mock<IAgentLlmService>();
        // STEP-02: Create context as AgentContext and get same from System.Text.Json.JsonSerializer.SerializeToElement.
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = AgentTestData.State(), Request = new() { Content = "Explain" },
            Roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88 } };
        var same = System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 88 });
        // STEP-03: Stub llm.RunAsync.
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([new("a", "get_roadmap", same), new("b", "get_roadmap", same)], null))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-04: Call Create().RunAsync and derive results from context.Exchanges.Single().Results.
        await Create(llm.Object).RunAsync(context, CancellationToken.None);

        var results = context.Exchanges.Single().Results;
        // STEP-05: Assert results[0].TryGetProperty() is false, results[1].status is "duplicate_request" and context.ToolsClosed is true.
        results[0].TryGetProperty("status", out _).Should().BeFalse();
        results[1].GetProperty("status").GetString().Should().Be("duplicate_request");
        context.ToolsClosed.Should().BeTrue();
    }

    // TEST-05: Explanation and clarification use existing context without calling ranking or routes.
    /// <summary>Checks the no-reranking path for ordinary conversational answers.</summary>
    [Theory]
    [InlineData("answer")][InlineData("clarify")]
    public async Task RunAsync_Answer_ReturnsWithoutTools(string action)
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer() with { Action = action }));
        // STEP-02: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        // STEP-03: Assert result.Snapshot is null and result.Reply is not empty.
        result.Snapshot.Should().BeNull(); result.Reply.Should().NotBeEmpty().And.NotContain("Please clarify");
        // STEP-04: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-06: Repair one invalid schema response and reject a second invalid response.
    /// <summary>Checks both successful repair and the single-repair ceiling.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task RunAsync_SchemaRepair_IsBounded(bool recover)
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.InvalidSchema));
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(recover ? new AgentLlmStep([], AgentTestData.Answer()) : AgentLlmStep.Failed(AgentLlmFailureKind.InvalidSchema));
        // STEP-02: Assert depending on recover, Create().RunAsync().Snapshot is null and Create().RunAsync() throws ServiceUnavailableException.
        if (recover) (await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)).Snapshot.Should().BeNull();
        else await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();
        // STEP-03: Verify RunAsync is called 2 times.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-07: Context size and cancellation prevent uncontrolled provider work.
    /// <summary>Checks resource caps before an LLM request and preserves caller cancellation.</summary>
    [Fact]
    public async Task RunAsync_OversizedContextAndCancellation_StopBeforeProvider()
    {
        // STEP-01: Mock IAgentLlmService.
        var llm = new Mock<IAgentLlmService>();
        // STEP-02: Get context from AgentTestData.Context and set context.Request.Content to a test value.
        var context = AgentTestData.Context(); context.Request.Content = new('x', 310000);
        // STEP-03: Wrap Create().RunAsync with context in act so its outcome can be asserted.
        var act = () => Create(llm.Object).RunAsync(context, CancellationToken.None);
        // STEP-04: Assert act throws ServiceUnavailableException.
        await act.Should().ThrowAsync<ServiceUnavailableException>();
        // STEP-05: Create cancellation as CancellationTokenSource and cancel cancellation.
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        // STEP-06: Assert Create().RunAsync() throws OperationCanceledException.
        await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), cancellation.Token))).Should().ThrowAsync<OperationCanceledException>();
        // STEP-07: Verify llm receives no other calls.
        llm.VerifyNoOtherCalls();
    }

    // TEST-08: A turn over the context limit only because of old chat leaves the oldest messages out of the prompt and still runs (conversation 217).
    /// <summary>The model reads the recent messages; the full history stays for the backend's evidence checks and the user's screen.</summary>
    [Fact]
    public async Task RunAsync_LongHistoryOverLimit_ShrinksPromptInsteadOfFailing()
    {
        // STEP-01: Create a context whose ten old messages alone pass a 20 000-character limit.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));
        var context = AgentTestData.Context();
        for (var index = 0; index < 10; index++) context.History.Add(new(index % 2 == 0 ? "user" : "assistant", new string('x', 3000)));

        // STEP-02: Run the turn with the small limit.
        await Create(llm.Object, new AgentLimits { MaxContextCharacters = 20000 }).RunAsync(context, CancellationToken.None);

        // STEP-03: Assert the provider was called, older messages were left out of the prompt and the history itself is whole.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        context.PromptHistorySkip.Should().BeGreaterThan(0);
        context.PromptHistory.Count().Should().BeGreaterThanOrEqualTo(4);
        context.History.Should().HaveCount(10);
    }

    // TEST-09: The total backend timeout cancels a slow provider and reports a controlled unavailable result.
    /// <summary>Verifies processing is bounded independently of provider HTTP timeouts.</summary>
    [Fact]
    public async Task RunAsync_TotalTimeout_CancelsSlowProvider()
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (AgentContext _, bool _, CancellationToken ct) => { await Task.Delay(Timeout.Infinite, ct); return (AgentLlmStep?)null; });
        // STEP-02: Wrap Create().RunAsync in act so its outcome can be asserted.
        var act = () => Create(llm.Object, new AgentLimits { TimeoutSeconds = 1 }).RunAsync(AgentTestData.Context(), CancellationToken.None);
        // STEP-03: Assert act throws ServiceUnavailableException and the thrown exception.ErrorCode is "AGENT_UNAVAILABLE".
        (await act.Should().ThrowAsync<ServiceUnavailableException>()).Which.ErrorCode.Should().Be("AGENT_UNAVAILABLE");
    }

    // TEST-10: Repeated tools stop at the round, aggregate-call and total-LLM-call caps.
    /// <summary>Verifies backend execution limits against a provider that never returns a final answer.</summary>
    [Theory]
    [InlineData("rounds", 2)][InlineData("calls", 1)][InlineData("llm", 2)]
    public async Task RunAsync_RepeatedTools_EnforcesEveryCallCap(string limit, int expectedCalls)
    {
        // STEP-01: Create limits as AgentLimits.
        var limits = new AgentLimits { MaxToolRounds = limit == "rounds" ? 1 : 10,
            MaxToolCalls = limit == "calls" ? 1 : 20, MaxLlmCalls = limit == "llm" ? 2 : 5 };
        // STEP-02: Mock IAgentLlmService.
        var llm = new Mock<IAgentLlmService>();
        // STEP-03: Create tool as AgentToolCall and derive tools from the expected value.
        var tool = new AgentToolCall("a", "get_roadmap", System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 88 }));
        var tools = limit == "calls" ? new[] { tool, tool with { Id = "b" } } : [tool];
        // STEP-04: Stub llm.RunAsync.
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep(tools, null));
        // STEP-05: Create context as AgentContext.
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = AgentTestData.State(), Request = new() { Content = "Explain" },
            Roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88 } };
        // STEP-06: Assert Create().RunAsync() throws ServiceUnavailableException and error.Message contains the expected text.
        var error = (await ((Func<Task>)(async () => await Create(llm.Object, limits).RunAsync(context, CancellationToken.None)))
            .Should().ThrowAsync<ServiceUnavailableException>()).Which;
        // A limit failure repeats if resent unchanged, so the message asks for clarification instead of a retry.
        error.Message.Should().Contain("điều chỉnh ngày, bữa, món").And.NotContain("thử lại");
        // STEP-07: Verify RunAsync is called times.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(expectedCalls));
    }

    // TEST-11: A model may request draft controls but cannot use those actions to also mutate state or items.
    /// <summary>Checks the chat intent allowlist and keeps backend lifecycle effects separate from proposal deltas.</summary>
    [Theory]
    [InlineData("discard_proposal", "discard")]
    [InlineData("reset_conversation", "reset")]
    public async Task RunAsync_DraftControl_ReturnsBackendAction(string action, string expected)
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(service => service.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = action, Reply = "Draft control" }));
        // STEP-02: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        // STEP-03: Assert result.Action is expected, result.Snapshot is null and result.Commit is null.
        result.Action.Should().Be(expected); result.Snapshot.Should().BeNull(); result.Commit.Should().BeNull();
    }

    // TEST-12: Non-schema failures never enter repair, including quota, transport, refusal and truncated output.
    /// <summary>Protects free provider quota while retaining a reason-specific API failure.</summary>
    [Theory]
    [InlineData(AgentLlmFailureKind.Configuration)][InlineData(AgentLlmFailureKind.Authentication)]
    [InlineData(AgentLlmFailureKind.Timeout)][InlineData(AgentLlmFailureKind.TurnTimeout)]
    [InlineData(AgentLlmFailureKind.Network)][InlineData(AgentLlmFailureKind.RateLimited)]
    [InlineData(AgentLlmFailureKind.RejectedRequest)][InlineData(AgentLlmFailureKind.ProviderUnavailable)]
    [InlineData(AgentLlmFailureKind.InvalidResponse)][InlineData(AgentLlmFailureKind.Refused)][InlineData(AgentLlmFailureKind.Truncated)]
    public async Task RunAsync_ProviderFailure_DoesNotRepair(AgentLlmFailureKind kind)
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentLlmStep.Failed(kind, 429, 45));
        // STEP-02: Wrap Create().RunAsync in act so its outcome can be asserted.
        var act = () => Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        // STEP-03: Assert act throws AgentProviderException, error.ErrorCode is "AGENT_UNAVAILABLE" and error.Failure is a new AgentLlmFailure.
        var error = (await act.Should().ThrowAsync<AgentProviderException>()).Which;
        error.ErrorCode.Should().Be("AGENT_UNAVAILABLE"); error.Failure.Should().Be(new AgentLlmFailure(kind, 429, 45));
        // STEP-04: Verify RunAsync is called once and RunAsync is never called.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()), Times.Once);
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-13: An unclassified missing response cannot be assumed to contain repairable model output.
    /// <summary>Fails closed without spending another provider request on an unknown failure.</summary>
    [Fact]
    public async Task RunAsync_NullResult_DoesNotRepair()
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync((AgentLlmStep?)null);
        // STEP-02: Wrap Create().RunAsync in act so its outcome can be asserted.
        var act = () => Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        // STEP-03: Assert act throws AgentProviderException and the thrown exception.Failure.Kind is AgentLlmFailureKind.InvalidResponse.
        (await act.Should().ThrowAsync<AgentProviderException>()).Which.Failure.Kind.Should().Be(AgentLlmFailureKind.InvalidResponse);
        // STEP-04: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-14: Persistent provider 503 responses stop after two retries and surface a provider error.
    /// <summary>Caps transient retries so an outage cannot consume the LLM call budget.</summary>
    [Fact]
    public async Task RunAsync_Repeated503_StopsAfterTwoRetries()
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.ProviderUnavailable, 503));
        // STEP-02: Assert Create().RunAsync() throws AgentProviderException.
        await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)))
            .Should().ThrowAsync<AgentProviderException>();
        // STEP-03: Verify RunAsync is called 3 times.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // TEST-15: A short per-minute 429 is retried once while a daily quota 429 fails immediately; covers rate_limit and daily_quota_exceeded.
    /// <summary>Distinguishes recoverable rate limits from exhausted quota before spending another provider call.</summary>
    [Theory]
    [InlineData("daily_quota_exceeded", false)]
    public async Task RunAsync_Gemini429_RetriesOnlyShortTemporaryLimits(string reason, bool recover)
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], null, Failure: new(AgentLlmFailureKind.RateLimited, 429, 1, "RESOURCE_EXHAUSTED", reason)))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer("Recovered")))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer("Recovered")));
        // STEP-02: Assert depending on recover, Create().RunAsync().Reply contains the expected text, Create().RunAsync().Reply contains the expected text and 1 more.
        if (recover) (await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)).Reply.Should().Contain("có trên ANGI").And.NotContain("Recovered");
        else await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None))).Should().ThrowAsync<AgentProviderException>();
        // STEP-03: Verify RunAsync is called 2 times.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()), Times.Exactly(recover ? 2 : 1));
    }

    // TEST-16: A complete create_proposal selection over an existing proposal updates it with fresh ranking provenance and no repair call.
    /// <summary>Accepts create_proposal in update mode as a scoped snapshot instead of forcing a patch repair.</summary>
    [Fact]
    public async Task RunAsync_CompleteSelectionOverExistingProposal_UpdatesWithoutActionRepair()
    {
        // STEP-01: Mock IAgentLlmService.
        var llm = new Mock<IAgentLlmService>();
        // STEP-02: Get context from AgentTestData.Context, get grant from AgentTestData.Grant and add grant to context.Grants.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        var grant = AgentTestData.Grant([1]);
        context.Grants.Add(grant);
        // STEP-03: Stub llm.RunAsync.
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Selection", Items = [AgentTestData.Item()] }));
        // STEP-04: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, CancellationToken.None);
        // STEP-05: Assert result.Snapshot.Items.Single().RequestId is grant.RequestId, result.Reply does not contain AgentReplyBuilder.RatingFallbackNote and 1 more.
        result.Snapshot!.Items.Single().RequestId.Should().Be(grant.RequestId);
        result.Snapshot.Notes.Should().NotContain(AgentReplyBuilder.RatingFallbackNote);
        context.RepairFeedback.Should().BeNull();
        // STEP-06: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-17: Caller cancellation during a provider call propagates without repair or an availability fallback.
    /// <summary>Keeps the UI Stop action distinct from server or provider timeouts.</summary>
    [Fact]
    public async Task RunAsync_CallerCancellationDuringProvider_Propagates()
    {
        // STEP-01: Create cancellation as CancellationTokenSource; mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>(); using var cancellation = new CancellationTokenSource();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (AgentContext _, bool _, CancellationToken ct) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, ct); return (AgentLlmStep?)null; });
        // STEP-02: Wrap Create().RunAsync in act so its outcome can be asserted.
        var act = () => Create(llm.Object).RunAsync(AgentTestData.Context(), cancellation.Token);
        // STEP-03: Assert act throws OperationCanceledException.
        await act.Should().ThrowAsync<OperationCanceledException>();
        // STEP-04: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-18: Failure telemetry includes safe diagnostic categories and timing without leaking prompt content.
    /// <summary>Makes future provider failures distinguishable without logging raw requests or upstream bodies.</summary>
    [Fact]
    public async Task RunAsync_ProviderFailure_RecordsSafeDiagnostics()
    {
        // STEP-01: Declare captured to capture a call argument; mock IAgentLlmService and IAgentTelemetry; stub telemetry.Record and llm.RunAsync.
        var llm = new Mock<IAgentLlmService>(); var telemetry = new Mock<IAgentTelemetry>(); object? captured = null;
        telemetry.Setup(t => t.Record("llm", It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<object>()))
            .Callback<string, long, long?, long?, object?>((_, _, _, _, data) => captured = data);
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.RateLimited, 429, 45));
        // STEP-02: Get context from AgentTestData.Context and set context.Request.Content to "private-prompt-must-not-leak".
        var context = AgentTestData.Context(); context.Request.Content = "private-prompt-must-not-leak";
        // STEP-03: Wrap Create().RunAsync with context in act so its outcome can be asserted.
        var act = () => Create(llm.Object, telemetry: telemetry.Object).RunAsync(context, CancellationToken.None);
        // STEP-04: Read diagnostic from the response; assert act throws AgentProviderException, diagnostic.RootElement.failureKind is "RateLimited" and 5 more.
        await act.Should().ThrowAsync<AgentProviderException>();
        using var diagnostic = System.Text.Json.JsonDocument.Parse(AgentJson.Write(captured));
        diagnostic.RootElement.GetProperty("failureKind").GetString().Should().Be("RateLimited");
        diagnostic.RootElement.GetProperty("httpStatus").GetInt32().Should().Be(429);
        diagnostic.RootElement.GetProperty("retryAfterSeconds").GetInt32().Should().Be(45);
        diagnostic.RootElement.GetProperty("callNumber").GetInt32().Should().Be(1);
        diagnostic.RootElement.GetProperty("repair").GetBoolean().Should().BeFalse();
        diagnostic.RootElement.GetProperty("elapsedMs").GetDouble().Should().BeGreaterThanOrEqualTo(0);
        // STEP-05: Assert AgentJson.Write() does not contain "private-prompt".
        AgentJson.Write(captured).Should().NotContain("private-prompt");
    }

    // TEST-19: A dish repeated on day 2 is swapped for an unused authorized dish without another LLM call, and a repeat with no unused alternative stays and is disclosed by the repeat note; covers both cases.
    /// <summary>Keeps multi-day plans varied for free while never blocking a plan whose ranked candidates cannot avoid a repeat.</summary>
    [Theory]
    [InlineData(false)]
    public async Task RunAsync_RepeatedDish_IsReplacedOnlyWhenAnUnusedCandidateExists(bool spare)
    {
        // STEP-01: Derive state from a copy of AgentTestData.State(), get context from AgentTestData.Context, set context.State to state and 1 more.
        var state = AgentTestData.State() with { MealSlots = ["lunch"] };
        var context = AgentTestData.Context(); context.State = state;
        context.Grants.Add(new(null, "lunch", spare ? [1, 2] : [1], Guid.NewGuid(), state));
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Draft", Items = [AgentTestData.Item(1, "a", 1), AgentTestData.Item(1, "b", 2)] }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2)]).RunAsync(context, default);

        // STEP-04: Assert result.Validation.Errors is empty, result.Snapshot.Items.Select(filtered) equals spare ? [1, 2] : [1, 1] and 3 more.
        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Select(item => item.DishId).Should().Equal(spare ? [1, 2] : [1, 1]);
        if (spare) result.Snapshot.Notes.Should().NotContain(AgentReplyBuilder.RepeatNote);
        else result.Snapshot.Notes.Should().Contain(AgentReplyBuilder.RepeatNote);
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-20: A whole-day taste proposed for "bữa 2 ngày 1" is stored as lunch of day 1, while an override already in state is not
    // narrowed by later edit wording such as "bữa 1 ngày 1"; covers both.
    /// <summary>Checks final-output narrowing applies only to the taste scope the model proposes from the user's taste wording this turn.</summary>
    [Theory]
    [InlineData(false)]
    public async Task RunAsync_WholeDayTasteForNamedMeal_IsNarrowedOnlyWhenNewlyProposed(bool newTaste)
    {
        // STEP-01: Get context from AgentTestData.Context, create dayTaste as AgentTasteOverride, derive state from a copy of AgentTestData.State() and 2 more.
        var context = AgentTestData.Context();
        var dayTaste = new AgentTasteOverride(1, null, [new("sour", .5)]);
        var state = AgentTestData.State() with { MealSlots = ["breakfast", "lunch", "dinner"] };
        context.State = newTaste
            ? state with { PendingPlanningRequest = new(["Tôi muốn ăn ở Đà Nẵng 2 ngày, tôi thích ăn chua"]) }
            : state with { TasteOverrides = [dayTaste] };
        context.Request.Content = newTaste ? "Vừa, áp dụng cho bữa 2 ngày 1" : "Đổi món khác cho bữa 1 ngày 1";
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            AgentTestData.Answer() with { State = state with { TasteOverrides = [new(1, null, [new("sour", .5, "Vừa")])] } }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.State.TasteOverrides.Select(filtered) equals the expected value.
        result.State.TasteOverrides.Select(x => (x.DayNumber, x.MealSlot))
            .Should().Equal(((short)1, newTaste ? "lunch" : null));
    }

    // TEST-21: An answer closes any older question, because the model read the message and moved on; covers an offer and a taste question.
    /// <summary>A question left open would be repeated and would block saving the plan.</summary>
    [Theory]
    [InlineData("Bạn có muốn xem lựa chọn khác không?")]
    [InlineData("Bạn muốn mức cay như thế nào: không, nhẹ, vừa, nhiều hay rất nhiều?")]
    public async Task RunAsync_AnswerWithOldQuestion_ClearsIt(string question)
    {
        // STEP-01: Get context from AgentTestData.Context, set context.Request.Content to a test text and set context.State to a copy of context.State.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1)));
        context.Request.Content = "Có những món gì?";
        context.State = context.State with { PendingQuestion = question };
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1)]).RunAsync(context, default);

        // STEP-04: Assert result.State.PendingQuestion is null.
        result.State.PendingQuestion.Should().BeNull();
    }

    // TEST-22: A question answered by the model no longer dumps the plan; when the model reads a wish to see a saved plan and writes {plan}, the backend lists it.
    /// <summary>Verifies the end-to-end behaviour of the answer action for both kinds of message; the user's wording is read by the model, not by a regex.</summary>
    [Theory]
    [InlineData("Vì sao chọn món này?", "Bạn nói rõ hơn giúp mình nhé.", false)]
    [InlineData("cho mình coi lại cái lịch phát", "Lịch của bạn đây nhé: {plan}", true)]
    public async Task RunAsync_AnswerAction_ListsPlanOnlyWhenAsked(string content, string reply, bool listed)
    {
        // STEP-01: Get context from AgentTestData.Context and set context.Request.Content to content.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item() with { DishName = "Mì Quảng" }));
        context.Request.Content = content;
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer(reply)));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply.Contains() is listed, the slot never reaches the user and an unrelated answer is the model's own.
        result.Reply.Contains("Mì Quảng").Should().Be(listed);
        result.Reply.Should().NotContain("{plan}");
        if (!listed) result.Reply.Should().Be(reply);
    }

    // TEST-22b: Keeping the plan after the budget offer saves the same dishes under the offered budget; any other new amount changes no draft.
    /// <summary>Fixes "Chốt đi" being refused as "lịch chưa theo ngân sách mới" after the user took the raise but kept the dishes (S1).</summary>
    [Theory]
    [InlineData(370000, true)]
    [InlineData(400000, false)]
    public async Task RunAsync_BudgetOfferKeptPlan_KeepsDishesUnderNewBudget(int budget, bool kept)
    {
        // STEP-01: Prepare a draft on a 300k budget whose last reply offered 370k.
        var source = AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1));
        source = source with { State = source.State with { BudgetAmount = 300000 } };
        var context = AgentTestData.Context(source);
        context.Request.Content = "không, giữ lịch này";
        context.State = source.State with { BudgetOffer = 370000 };
        // STEP-02: Mock IAgentLlmService; the model answers with the new budget.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer("Mình giữ nguyên lịch nhé.") with { State = context.State with { BudgetAmount = budget, BudgetOffer = null } }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1)]).RunAsync(context, default);

        // STEP-04: Assert only the offered amount yields a draft with the same dish and the offer cleared.
        if (kept)
        {
            result.Snapshot.Should().NotBeNull();
            result.Snapshot!.Items.Select(item => item.DishId).Should().Equal(1);
            result.Snapshot.State.BudgetAmount.Should().Be(370000);
            result.State.BudgetOffer.Should().BeNull();
        }
        else result.Snapshot.Should().BeNull();
    }

    // TEST-23: Items attached to a question are dropped rather than failing the turn, and never reach a proposal.
    /// <summary>A clarify or answer can never edit a plan, so its stray items are simply ignored.</summary>
    [Fact]
    public async Task RunAsync_QuestionWithItems_IgnoresItems()
    {
        // STEP-01: Get context from AgentTestData.Context.
        var context = AgentTestData.Context();
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer() with { Items = [AgentTestData.Item()] }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Snapshot is null and result.Reply has a value.
        result.Snapshot.Should().BeNull();
        result.Reply.Should().NotBeNullOrWhiteSpace();
    }

    // TEST-24: Answering the preference question closes the intake and plans in the same turn; "không" reaches the model marked as adding no requirement.
    /// <summary>Ensures "không" is not read as a dismissal and that the plan is not blocked as an unrequested change on an empty roadmap.</summary>
    [Fact]
    public async Task RunAsync_PreferenceAnswer_ClosesIntakeAndPlans()
    {
        // STEP-01: Create state as AgentState, get context from IntakeContext and add the computed value to context.Grants.
        var state = new AgentState { Province = "Da Nang", NumDays = 1, BudgetAmount = 500000, MealSlots = ["lunch"], PlanningIntake = AgentIntakePolicy.Preferences,
            PendingQuestion = AgentIntakePolicy.PreferencesQuestion, PendingPlanningRequest = new(["lap lich cho toi"]) };
        var context = IntakeContext("không", state, AgentTestData.Snapshot());
        context.Grants.Add(new(null, null, [1], Guid.NewGuid(), state));
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Xong rồi nhé!", Items = [AgentTestData.Item()] }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        // STEP-04: Assert context.IntakeComplete is true, result.Snapshot is not null, result.State.PlanningIntake is null and result.State.PendingQuestion is null.
        context.IntakeComplete.Should().BeTrue();
        result.Snapshot.Should().NotBeNull();
        result.State.PlanningIntake.Should().BeNull();
        result.State.PendingQuestion.Should().BeNull();
    }

    // TEST-25: With no plan to disturb, the user's own answer is applied without asking them to confirm what they just said; a budget already set is still protected.
    /// <summary>Answering the questions with a new duration or "no limit", including slang, moves straight on, because the model read it.</summary>
    [Theory]
    [InlineData("lập lịch Hà Nội 5 ngày", 5, false)]
    [InlineData("Hà Nội, không giới hạn", 3, true)]
    [InlineData("Hà Nội, xả láng", 3, true)]
    public async Task RunAsync_AnswerWithoutAPlan_AppliesWithoutConfirmation(string content, short numDays, bool unrestricted)
    {
        // STEP-01: Get result from a model that read the answer.
        var state = new AgentState { NumDays = 3, PlanningIntake = AgentIntakePolicy.Details };
        var result = await Create(Scripted(Asks("trip_details", "Bạn cho mình biết thêm được không?", state with { Province = "Hà Nội", NumDays = numDays, BudgetUnrestricted = unrestricted })).Object, intakeCatalog: IntakeCatalog())
            .RunAsync(IntakeContext(content, state), default);

        // STEP-02: Assert result.State.PendingStateChange is null, result.State.NumDays is numDays, result.State.BudgetUnrestricted is unrestricted and 1 more.
        result.State.PendingStateChange.Should().BeNull();
        result.State.NumDays.Should().Be(numDays);
        result.State.BudgetUnrestricted.Should().Be(unrestricted);
        result.State.Province.Should().Be("Hà Nội");
    }

    // TEST-26: A plan already on screen skips the questions without reading provinces or places; only the reply check reads dish names.
    /// <summary>Ensures edits keep their existing path and ask nothing extra.</summary>
    [Fact]
    public async Task RunAsync_ExistingPlan_SkipsIntake()
    {
        // STEP-01: Get context from AgentTestData.Context and set context.Request.Content to a test text.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.Request.Content = "lập lịch lại";
        // STEP-02: Mock ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository and IAgentLlmService; stub GetDishNamesAsync and llm.RunAsync.
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>(MockBehavior.Strict);
        catalog.Setup(x => x.GetDishNamesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-03: Call Create().RunAsync.
        await Create(llm.Object, intakeCatalog: catalog.Object).RunAsync(context, default);

        // STEP-04: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-27: Over a plan a day moves through locate_places only as far as the plan allows; covers a whole-trip area, a day past the trip, and a place the day already has.
    /// <summary>The model reads "ngày 9 ở Hoàn Kiếm"; the backend decides what it may change, and an unchanged itinerary forces no replan.</summary>
    [Theory]
    [InlineData("area")]
    [InlineData("past")]
    [InlineData("same")]
    public async Task RunAsync_LocatePlacesOverPlan_MovesOnlyWhatItMay(string area)
    {
        // STEP-01: Get context over a one-day plan and script the model.
        var plan = AgentTestData.Snapshot(AgentTestData.Item());
        plan = plan with { State = plan.State with { Province = "Hà Nội", NumDays = 1, DayAreas = area == "same" ? [new(1, "Hoàn Kiếm", 21.03m, 105.85m, 3m)] : null } };
        var context = AgentTestData.Context(plan);
        context.State = plan.State;
        context.Request.Content = "ngày đó tôi ở Hoàn Kiếm";
        var call = area switch { "area" => Locates("Hoàn Kiếm"), "past" => Locates(null, new AgentLocateStop([9], "Hoàn Kiếm")), _ => Locates(null, new AgentLocateStop([1], "Hoàn Kiếm")) };
        var llm = Scripted(call, new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-02: Call Create().RunAsync.
        await Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        // STEP-03: Assert no day moved, a whole-trip area was refused and a day past the trip is named back without extending it.
        context.ItineraryChanged.Should().BeFalse();
        context.State.NumDays.Should().Be(1);
        if (area == "area") context.Exchanges.Single().Results[0].GetProperty("status").GetString().Should().Be("invalid_arguments");
        if (area == "past") context.Exchanges.Single().Results[0].GetProperty("outsideTrip")[0].GetInt16().Should().Be(9);
    }

    // TEST-28: A place the model read over an existing plan moves that day through locate_places, and the proposal for it is accepted.
    /// <summary>Ensures "ngày 1 tôi ở Hoàn Kiếm" changes the plan in whatever wording the model understood it.</summary>
    [Fact]
    public async Task RunAsync_ItineraryOnExistingPlan_MovesDayAndAcceptsProposal()
    {
        // STEP-01: Get source from AgentTestData.Snapshot, get context from AgentTestData.Context, set context.State to a copy of context.State and 5 more.
        var source = AgentTestData.Snapshot(AgentTestData.Item());
        var context = AgentTestData.Context(source);
        context.State = context.State with { Province = "Hà Nội", NumDays = 1, MealSlots = ["lunch"] };
        context.Request.Content = "hôm đó tôi qua Hoàn Kiếm chơi";
        var dish = AgentTestData.Dish(2);
        dish.Restaurant.ProvinceName = "Hà Nội"; dish.Restaurant.Latitude = 21.03m; dish.Restaurant.Longitude = 105.85m;
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        var calls = 0;
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            if (calls++ == 0) return Task.FromResult<AgentLlmStep?>(Locates(null, new AgentLocateStop([1], "Hoàn Kiếm")));
            context.Grants.Clear();
            context.Grants.Add(new(1, "lunch", [2], null, context.State));
            return Task.FromResult<AgentLlmStep?>(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Xong rồi", Scope = new(1, "lunch"), Items = [AgentTestData.Item(2, "a")] }));
        });

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalog: [dish], intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        // STEP-04: Assert context.ItineraryChanged is true, result.Validation.Errors is empty and 2 more.
        context.ItineraryChanged.Should().BeTrue();
        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.State.DayAreas!.Single().Label.Should().Be("Hoàn Kiếm");
        result.Snapshot.Items.Single().DishId.Should().Be(2);
    }

    // TEST-29: Complaints about a dish, and any plan without itinerary places, keep the model's normal edit flow.
    /// <summary>Guards the rule that the new correction case does not take over edits handled before.</summary>
    [Theory]
    [InlineData("bữa trưa ngày 1 sai vị rồi", true)]
    [InlineData("món này sai rồi", true)]
    [InlineData("ngày 1 sai rồi", false)]
    public async Task RunAsync_ComplaintAboutDishOrPlanWithoutPlaces_KeepsModelFlow(string content, bool withPlaces)
    {
        // STEP-01: Derive context from the expected value.
        var context = withPlaces ? WrongDayOneContext(content) : IntakeContext(content, AgentTestData.State() with { Province = "Hà Nội" }, AgentTestData.Snapshot(AgentTestData.Item()));
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-03: Call Create().RunAsync.
        await Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        // STEP-04: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        // STEP-05: Assert context.ItineraryChanged is false.
        context.ItineraryChanged.Should().BeFalse();
    }

    // TEST-30: Changing taste after a proposal replans with the new taste, keeps the itinerary places although the model's state cannot carry them, and accepts the new dishes.
    /// <summary>Covers the question "người dùng bỗng đổi khẩu vị khi đã có đề xuất": every changed position needs a fresh search under the new taste.</summary>
    [Fact]
    public async Task RunAsync_TasteChangedOnProposalWithItinerary_ReplansAndKeepsPlaces()
    {
        // STEP-01: Derive before from a copy of AgentTestData.State(), create source as AgentSnapshot, get context from AgentTestData.Context and 3 more.
        var before = AgentTestData.State() with { NumDays = 1, MealSlots = ["lunch"], TasteTargets = [new("spicy", .25)], DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 3m)] };
        var source = new AgentSnapshot(before, [AgentTestData.Item() with { DishName = "Dish 1" }], []);
        var context = AgentTestData.Context(source);
        context.State = before;
        context.Request.Content = "đổi khẩu vị sang ngọt nhẹ cho cả chuyến";
        var fresh = AgentTestData.Dish(2);
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            var changed = context.State with { TasteTargets = [new("sweet", .25, "ngọt nhẹ")], DayAreas = null };
            context.Grants.Clear();
            context.Grants.Add(new(1, "lunch", [2], Guid.NewGuid(), context.State with { TasteTargets = [new("sweet", .25)] }));
            return Task.FromResult<AgentLlmStep?>(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Xong rồi", State = changed, Scope = new(1, null), Items = [AgentTestData.Item(2, "a")] }))!;
        });

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(), fresh], intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        // STEP-04: Assert result.Validation.Errors is empty, result.Snapshot.Items.Single().DishId is 2 and 3 more.
        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Single().DishId.Should().Be(2);
        result.Snapshot.State.TasteTargets.Should().ContainSingle(target => target.Dimension == "sweet" && target.Target == .25);
        result.Snapshot.State.DayAreas!.Single().Label.Should().Be("Hoàn Kiếm");
        result.Snapshot.Notes.Should().Contain(note => note.Contains("ngọt mức nhẹ"));
    }

    // TEST-31: The current location shared at the scope question keeps its coordinates and radius through the model's next question.
    /// <summary>Ensures the location button works at this question, where the province is already known.</summary>
    [Fact]
    public async Task RunAsync_ScopeAnsweredByLocation_KeepsCoordinates()
    {
        // STEP-01: Create located as AgentState and script the model.
        var located = new AgentState { Province = "Hà Nội", PlanningIntake = AgentIntakePolicy.Scope, OriginMode = "gps", OriginLatitude = 21.03m, OriginLongitude = 105.85m, OriginLabel = "Vị trí hiện tại", RadiusKm = 10m };
        var sut = Create(Scripted(Asks("trip_details", "Bạn đi mấy ngày nè?")).Object, intakeCatalog: IntakeCatalog(), places: Places());

        // STEP-02: Run sut.RunAsync and keep result.
        var result = await sut.RunAsync(IntakeContext("Dùng vị trí hiện tại của mình", located), default);

        // STEP-03: Assert result.Reply is the model's question and result.State matches the condition.
        result.Reply.Should().Be("Bạn đi mấy ngày nè?");
        result.State.Should().Match<AgentState>(x => x.OriginMode == "gps" && x.RadiusKm == 10m && x.PlanningIntake == AgentIntakePolicy.Details);
    }

    // TEST-32: A trip that already has a province keeps its search as it is, so a user who widened the search is not narrowed again.
    /// <summary>Only the first contact with a pin fills the circle.</summary>
    [Fact]
    public async Task RunAsync_PinWithProvince_KeepsSearch()
    {
        // STEP-01: Create state as AgentState and get result from Create().RunAsync.
        var state = new AgentState { Province = "Hà Nội", NumDays = 7, OriginMode = "pinned", OriginLatitude = 21.03m, OriginLongitude = 105.85m };

        var result = await Create(Scripted(Asks("trip_details", "Ngân sách của bạn khoảng bao nhiêu?")).Object, intakeCatalog: IntakeCatalog(), places: Places())
            .RunAsync(IntakeContext("lập lịch cho tôi", state), default);

        // STEP-02: Assert result.State.RadiusKm is null.
        result.State.RadiusKm.Should().BeNull();
    }

    // TEST-33: A dish suggested in the middle of the planning questions keeps the trip values and the questions' step; the old question is not glued onto the model's reply.
    /// <summary>
    /// In conversation 160 "tôi không biết" was misread as asking for a dish and the plan being built was lost; one wrong reading must not cost the user their place.
    /// Conversation 196 showed the glued question under every suggestion, so the step lives in the state and the question in the history the model reads.
    /// </summary>
    [Fact]
    public async Task RunAsync_DishSuggestionDuringIntake_KeepsQuestionAndTrip()
    {
        // STEP-01: Create state as AgentState, get context from IntakeContext, get llm from WordingLlm and get result from Suggesting().RunAsync.
        var state = new AgentState { Province = "Da Nang", NumDays = 7, BudgetAmount = 3500000, PlanningIntake = AgentIntakePolicy.Preferences, PendingQuestion = AgentIntakePolicy.PreferencesQuestion };
        var context = IntakeContext("không nhưng tôi chỉ muốn gợi ý 1 món bất kì chứ có phải lập lịch đâu nhỉ", state);
        var llm = WordingLlm(context, new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = "Vậy bạn thử {name1} ở {restaurant1} nhé, khoảng {price1}." }));

        var result = await Suggesting(llm.Object, intake: true).RunAsync(context, default);

        // STEP-02: Assert result.Reply contains "Dish 1" without the question, result.Snapshot is null and result.State keeps the trip and the step.
        result.Reply.Should().Contain("Dish 1").And.NotContain(AgentIntakePolicy.PreferencesQuestion);
        result.Snapshot.Should().BeNull();
        result.State.Should().Match<AgentState>(x => x.PlanningIntake == AgentIntakePolicy.Preferences && x.PendingQuestion == null && x.NumDays == 7 && x.BudgetAmount == 3500000);
    }

    /// <summary>Records what each provider call was allowed to do, because the context object is reused through the turn.</summary>
    private static Mock<IAgentLlmService> Phases(List<(AgentTurnPhase Phase, string? Tool)> seen, params AgentLlmStep[] steps)
    {
        var llm = new Mock<IAgentLlmService>();
        var next = new Queue<AgentLlmStep>(steps);
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<AgentContext, bool, CancellationToken>((context, _, _) => seen.Add((context.Phase, context.ForcedTool)))
            .ReturnsAsync(() => next.Dequeue());
        return llm;
    }

    // TEST-34: A chat message is read with no tools, a named tool is then the only one, and following calls have all tools again.
    /// <summary>A model holding tools calls one even when none fits: "1 + 1 bằng mấy" once became a dish suggestion.</summary>
    [Fact]
    public async Task RunAsync_ChatMessage_ReadsThenForcesTheNamedTool()
    {
        // STEP-01: Get context and script a read asking for a tool, the tool call, and the final answer.
        var context = IntakeContext("ở Cầu Giấy", new AgentState());
        var seen = new List<(AgentTurnPhase Phase, string? Tool)>();
        var llm = Phases(seen, new AgentLlmStep([], new AgentOutput { Action = "use_tool", Reply = "", Tool = "locate_places" }), Locates("Cầu Giấy"),
            Asks("trip_details", "Bạn đi mấy ngày nè?"));

        // STEP-02: Call Create().RunAsync.
        await Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        // STEP-03: Assert the phases of the three calls and that the tool ran.
        seen.Should().Equal((AgentTurnPhase.Read, null), (AgentTurnPhase.Forced, "locate_places"), (AgentTurnPhase.Act, null));
        context.Exchanges.Should().ContainSingle();
    }

    // TEST-56: The state the read step gave reaches the forced call and is gone once the tool has run.
    /// <summary>Conversation 558: the read put the ten suggested dishes in mealRequests, the forced call, which never saw that reading, sent none.</summary>
    [Fact]
    public async Task RunAsync_ChatMessage_ForcedCallSeesTheReadState()
    {
        // STEP-01: Get context and script a read with a state, the tool call, and the final answer, recording the read state of each call.
        var context = IntakeContext("ở Cầu Giấy", new AgentState());
        var read = new AgentState { Province = "Thành phố Hà Nội", NumDays = 3 };
        var seen = new List<AgentState?>();
        var next = new Queue<AgentLlmStep>([new AgentLlmStep([], new AgentOutput { Action = "use_tool", Reply = "", Tool = "locate_places", State = read }),
            Locates("Cầu Giấy"), Asks("trip_details", "Bạn đi mấy ngày nè?")]);
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<AgentContext, bool, CancellationToken>((current, _, _) => seen.Add(current.ReadState))
            .ReturnsAsync(() => next.Dequeue());

        // STEP-02: Call Create().RunAsync.
        await Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        // STEP-03: Assert only the forced call carried the read state.
        seen.Should().HaveCount(3);
        seen[0].Should().BeNull();
        seen[1].Should().BeSameAs(read);
        seen[2].Should().BeNull();
    }

    // TEST-35: The steps that already know the work skip the read; covers a button command, a submitted form and a replan after confirmation.
    /// <summary>Reading again would only spend a call on a message that carries no new meaning.</summary>
    [Theory]
    [InlineData("button")][InlineData("form")][InlineData("replan")]
    public async Task RunAsync_KnownWork_SkipsTheRead(string kind)
    {
        // STEP-01: Get context for the kind and script the answer.
        var context = IntakeContext("ok", new AgentState());
        if (kind == "button") context.Request.Command = "alternative";
        if (kind == "form") context.Request.ConfirmedState = new AgentState();
        if (kind == "replan") context.ReplanAfterConfirmation = true;
        var seen = new List<(AgentTurnPhase Phase, string? Tool)>();
        var llm = Phases(seen, new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-02: Call Create().RunAsync.
        await Create(llm.Object).RunAsync(context, default);

        // STEP-03: Assert the only call offered all tools.
        seen.Should().Equal((AgentTurnPhase.Act, null));
    }

    // TEST-36: Agreeing with nothing waiting never reaches the user as a claim: with a ready plan it becomes a wish to save, which only asks; with no plan it stays an answer;
    // with a clarification waiting it answers that question and the model's reply is shown.
    /// <summary>Conversation 194 answered "chốt đi" with "Chốt nhé!" and saved nothing; conversation 198 answered "giữ nguyên" to "muốn đổi món nào?" with the save question.</summary>
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("ready", AgentConfirmationPolicy.AwaitingQuestion, null)]
    public async Task RunAsync_AcceptWithNothingWaiting_BecomesASaveWish(string? status, string? awaited, string? action)
    {
        // STEP-01: Get a context with or without a ready proposal and an open question, and script the reading.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = snapshot.State, Request = new ANGI.Application.DTOs.Agent.SendAgentMessageRequestDto { Content = "chốt đi" },
            CurrentProposal = status is null ? null : new(9, 2, "create", status, snapshot, new AgentValidation([], [], 0, []), DateTime.UtcNow.AddHours(1)), AwaitedAnswer = awaited };
        var llm = Scripted(new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = "Chốt nhé!", PendingAnswer = "accept" }));

        // STEP-02: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-03: Assert the action, and that a reply to an open question is the model's own.
        result.Action.Should().Be(action);
        if (awaited is not null) result.Reply.Should().Be("Chốt nhé!");
    }

    // TEST-37: A tool asked for outside the read step is refused with the usual question, not run.
    /// <summary>Stops the use_tool action from becoming a way round the tool limits.</summary>
    [Fact]
    public async Task RunAsync_UseToolOutsideRead_IsNotRun()
    {
        // STEP-01: Get a replanning context and script the action.
        var context = IntakeContext("ok", new AgentState());
        context.ReplanAfterConfirmation = true;
        var llm = Scripted(new AgentLlmStep([], new AgentOutput { Action = "use_tool", Reply = "", Tool = "suggest_dish" }));

        // STEP-02: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-03: Assert no tool ran.
        context.Exchanges.Should().BeEmpty();
        result.Snapshot.Should().BeNull();
    }

    // TEST-38: A dish already suggested in this conversation is left out, so asking again gives another one.
    /// <summary>"Bất kì" must not repeat the same dish forever.</summary>
    [Fact]
    public async Task RunAsync_RepeatedSuggestion_GivesAnotherDish()
    {
        // STEP-01: Get context from IntakeContext, add the computed value to context.History, get llm from WordingLlm and get result from Suggesting().RunAsync.
        var context = IntakeContext("gợi ý một món", new AgentState());
        context.History.Add(new("assistant", "Mình gợi ý Dish 1 ở Real restaurant, khoảng 50.000 đồng."));
        var llm = WordingLlm(context, new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = "Thử {name1} ở {restaurant1} nhé, khoảng {price1}." }));

        var result = await Suggesting(llm.Object).RunAsync(context, default);

        // STEP-02: Assert result.Reply contains "Dish 2" and result.State.DishOffered is true.
        result.Reply.Should().Contain("Dish 2").And.NotContain("Dish 1 ");
        result.State.DishOffered.Should().BeTrue();
    }

    // TEST-39: The model reads every message that used to get a backend reply, and its own wording is shown; covers greetings, thanks, help, vague edits, a bare "ok", tastes, novelty and a missing meal, with no trip, over an empty roadmap and over a plan.
    /// <summary>The user asked for the model to hold the conversation, with the backend guarding from behind.</summary>
    [Theory]
    [InlineData("none", "alo")]
    [InlineData("none", "Xin chào")]
    [InlineData("none", "cảm ơn nha")]
    [InlineData("none", "bạn là ai")]
    [InlineData("none", "hướng dẫn tôi")]
    [InlineData("none", "tôi thích ngọt")]
    [InlineData("none", "muốn thử món lạ")]
    [InlineData("frame", "Nhưng tôi thích ăn cay")]
    [InlineData("frame", "nhẹ")]
    [InlineData("plan", "alo")]
    [InlineData("plan", "ok")]
    [InlineData("plan", "đổi cái đó đi")]
    [InlineData("plan", "cái này không ngon")]
    [InlineData("plan", "tôi thích ăn cay")]
    [InlineData("plan", "bớt cay")]
    [InlineData("plan", "muốn thử món lạ")]
    [InlineData("plan", "bữa 5 ngày 1 đổi món")]
    [InlineData("plan", "bỏ bữa sáng")]
    public async Task RunAsync_MessageWithoutFacts_IsReadAndWordedByTheModel(string trip, string content)
    {
        // STEP-01: Create state from trip and get context from IntakeContext.
        var state = trip switch { "plan" => AgentTestData.State(), "frame" => new AgentState { NumDays = 3, Title = "Khám phá ẩm thực Hà Nội" }, _ => new AgentState() };
        var context = IntakeContext(content, state, trip == "plan" ? AgentTestData.Snapshot(AgentTestData.Item()) : null);
        const string reply = "Mình nghe đây nè, bạn muốn mình giúp gì thêm không?";
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "chat", Reply = reply }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        // STEP-04: Assert result.Reply is reply, result.Snapshot is null and result.State.PendingQuestion is null.
        result.Reply.Should().Be(reply);
        result.Snapshot.Should().BeNull();
        result.State.PendingQuestion.Should().BeNull();
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-40: The model's question is shown and stays open. Before any plan, a taste question is a planning step and is recorded, so "cay vừa"
    // next turn plans instead of suggesting one dish ("Lập lịch hộ tôi, tôi thích ăn cay"); a vague edit or novelty question records nothing.
    /// <summary>The answer to a taste question asked while planning stays part of that plan.</summary>
    [Theory]
    [InlineData("taste_intensity", "Bạn thích cay nhẹ hay vừa?", true)]
    [InlineData("taste_scope", "Bạn muốn cay cho cả chuyến hay riêng ngày nào?", true)]
    [InlineData("edit_scope", "Bạn muốn đổi món nào nè?", true)]
    [InlineData("novelty", "Món lạ là khác lịch này hay món bạn chưa từng ăn?", true)]
    public async Task RunAsync_ModelQuestion_IsShownAndTasteQuestionsStartPlanning(string kind, string question, bool shown)
    {
        // STEP-01: Create state as an empty roadmap, get context from IntakeContext and mock IAgentLlmService; stub llm.RunAsync.
        var context = IntakeContext("tôi thích ăn cay", new AgentState { NumDays = 3 });
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "clarify", Clarification = kind, Reply = question }));

        // STEP-02: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        // STEP-03: Assert result.Reply, that the question stays open and that no planning stage opened.
        if (shown) result.Reply.Should().Be(question);
        else result.Reply.Should().NotContain("mức 7");
        result.State.PendingQuestion.Should().Be(result.Reply);
        var planning = kind is "taste_intensity" or "taste_scope";
        // No province yet, so the stage stays at the trip details.
        result.State.PlanningIntake.Should().Be(planning ? AgentIntakePolicy.Details : null);
        (result.State.PendingPlanningRequest is not null).Should().Be(planning);
    }

    // TEST-41: Planning starts only with a plan, a planning question or a request being clarified; trip values from the roadmap alone do not count.
    /// <summary>In a chat opened on a roadmap, "tôi thích ăn cay" is still a dish idea (conversation 220).</summary>
    [Fact]
    public void PlanningStarted_IgnoresRoadmapValues()
    {
        // STEP-01: Create a context holding only the roadmap's province, days and date.
        var context = AgentTestData.Context();
        context.State = new AgentState { Province = "Hà Nội", NumDays = 3, StartDate = new(2026, 11, 10) };

        // STEP-02: Assert planning has not started, then starts once a planning request is being clarified.
        AgentIntakePolicy.PlanningStarted(context).Should().BeFalse();
        context.State = context.State with { PendingPlanningRequest = AgentIntakePolicy.CaptureRequest(null, "lập lịch hộ tôi") };
        AgentIntakePolicy.PlanningStarted(context).Should().BeTrue();
    }

    // TEST-42: A suggestion with conditions costs two provider calls: the model reads the conditions, then words the reply from the tool's facts; a state it returns is ignored.
    /// <summary>The user reads the model's own sentence while the plan and trip values stay untouched.</summary>
    [Fact]
    public async Task RunAsync_ConditionedSuggestion_ShowsModelWordingAndKeepsState()
    {
        // STEP-01: Create state as AgentState, get context from IntakeContext and create reply.
        var state = new AgentState { Province = "Da Nang" };
        var context = IntakeContext("gợi ý món cay dưới 60k", state);
        const string reply = "Mình thấy {name1} ở {restaurant1} khoảng {price1} khá vừa ý bạn đó!";
        // STEP-02: Get llm from WordingLlm.
        var llm = WordingLlm(context, new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = reply, State = new AgentState { Province = "Hue" } }));

        // STEP-03: Get result from Suggesting().RunAsync.
        var result = await Suggesting(llm.Object, intake: true).RunAsync(context, default);

        // STEP-04: Assert result.Reply is the filled wording, result.Action is "chat", result.State.Province is "Da Nang" and result.State.PendingPlanningRequest is null.
        result.Reply.Should().Be("Mình thấy Dish 1 ở Real restaurant khoảng 50.000 đồng khá vừa ý bạn đó!");
        result.Action.Should().Be("chat");
        result.State.Province.Should().Be("Da Nang");
        result.State.PendingPlanningRequest.Should().BeNull();
        // STEP-05: Verify RunAsync is called twice and tools are closed for the second call.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        context.ToolsClosed.Should().BeTrue();
    }

    // TEST-43: A long trip is assembled by the backend after the one search, however long: one provider call and no paid route requests; covers just past the threshold, 30 days and the maximum.
    /// <summary>A model reply cannot hold hundreds of items, so these trips would otherwise end in a truncated output.</summary>
    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(100)]
    public async Task RunAsync_LongTrip_BackendAssemblesAfterOneSearch(int days)
    {
        // STEP-01: Create state as AgentState, get context from IntakeContext, set context.IntakeComplete to true, create search as AgentRecommendationInput and 1 more.
        var state = new AgentState { Province = "Da Nang", NumDays = (short)days, BudgetAmount = 99999999, MealSlots = ["breakfast", "lunch", "dinner"] };
        var context = IntakeContext("lập lịch đi", state);
        context.IntakeComplete = true;
        var search = new AgentRecommendationInput(Scope: "full", TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [], TopK: 30);
        var call = new AgentToolCall("r", "get_recommendation", System.Text.Json.JsonSerializer.SerializeToElement(search, AgentJson.Options));
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([call], null));
        // STEP-03: Get catalog from Range().Select(filtered).ToArray and create fixture through AgentTestData.Validator.
        var catalog = Enumerable.Range(1, 40).Select(id => AgentTestData.Dish(id)).ToArray();
        var fixture = AgentTestData.Validator(catalog);
        // STEP-04: Stub fixture.GetCandidateIdsAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<ANGI.Application.Common.Models.Discovery.DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(catalog.Select(dish => dish.Id).ToArray());
        // STEP-05: Create policy as AgentConstraintGuard and create limits as AgentLimits.
        var policy = new AgentConstraintGuard();
        var limits = new AgentLimits();
        // STEP-06: Mock IAgentTelemetry.
        var telemetry = Mock.Of<IAgentTelemetry>();
        // STEP-07: Create tools as AgentToolExecutor and get result from new AgentOrchestrator().RunAsync.
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), policy, new AgentRouteEstimator(fixture.Routes.Object), limits, telemetry);

        var result = await new AgentOrchestrator(llm.Object, tools, policy, new(), fixture.Validator, limits, telemetry).RunAsync(context, default);

        // STEP-08: Assert result.Validation.Errors is empty, result.Snapshot.Items has days * 3 item(s) and 1 more.
        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Should().HaveCount(days * 3);
        result.Snapshot.Items.Select(item => (item.DayNumber, item.MealSlot)).Distinct().Should().HaveCount(days * 3);
        // STEP-09: Verify RunAsync is called once and CalculateRouteAsync is never called.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Routes.Verify(x => x.CalculateRouteAsync(It.IsAny<ANGI.Application.Common.Models.Roadmap.RouteCoordinate?>(), It.IsAny<IReadOnlyList<ANGI.Application.Common.Models.Roadmap.RouteCoordinate>>(),
            It.IsAny<ANGI.Application.Common.Models.Roadmap.RouteProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-44: The duration limit reads up to 100 days in a message and refuses just above it.
    /// <summary>"30 ngày" used to be refused by the form and "100 ngày" could not even be read.</summary>
    [Theory]
    [InlineData("lập lịch Hà Nội 30 ngày", 30)]
    [InlineData("lập lịch Hà Nội 100 ngày", 100)]
    public async Task RunAsync_DurationUpToMaximum_IsAccepted(string content, int days)
    {
        // STEP-01: Get result from Create().RunAsync.
        var llm = Scripted(Asks("trip_details", "Ngân sách của bạn khoảng bao nhiêu?", new AgentState { Province = "Hà Nội", NumDays = (short)days }));
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(IntakeContext(content, new AgentState()), default);

        // STEP-02: Assert result.State.NumDays is days and result.Reply does not contain the expected text.
        result.State.NumDays.Should().Be((short)days);
        result.Reply.Should().NotContain("Số ngày phải");
    }

    // TEST-45: The model's question about missing trip details is shown when backed, and replaced by the fixed one when it invents a figure; the question stays open in both.
    /// <summary>The wording may answer what the user said, while the facts stay checked.</summary>
    [Theory]
    [InlineData("Không sao đâu, bạn ước chừng một mức cho mỗi người được không?", true)]
    public async Task RunAsync_TripQuestionFromModel_IsShownOnlyWhenBacked(string reply, bool shown)
    {
        // STEP-01: Get context from IntakeContext.
        var context = IntakeContext("mình chưa biết nữa", BudgetQuestionState() with { NumDays = 3 });
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "clarify", Clarification = "trip_details", Reply = reply }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        // STEP-04: Assert result.Reply is the expected value, result.State.PendingQuestion is result.Reply and result.State.PlanningIntake is AgentIntakePolicy.Details.
        result.Reply.Should().Be(shown ? reply : AgentReplyBuilder.TripDetailsQuestion(context.State));
        result.State.PendingQuestion.Should().Be(result.Reply);
        result.State.PlanningIntake.Should().Be(AgentIntakePolicy.Details);
    }

    // TEST-46: An off-topic or social reply during the budget question keeps the whole request, so the next message continues where the user left off;
    // only the backend's refusal repeats the question, while a social reply is the model's own words.
    /// <summary>Passing the reply to the model must not lose the user's place in the questions.</summary>
    [Theory]
    [InlineData("chat")]
    public async Task RunAsync_AsideDuringBudgetQuestion_KeepsThePlace(string action)
    {
        // STEP-01: Create pending as AgentPendingPlanningRequest and get context from IntakeContext.
        var pending = new AgentPendingPlanningRequest(["Lập lịch Hà Nội 3 ngày, ăn cay vừa"]);
        var context = IntakeContext("code giúp tôi", BudgetQuestionState() with { PendingPlanningRequest = pending });
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = action, Reply = "Dạ bạn." }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        // STEP-04: Assert result.State.PlanningIntake is AgentIntakePolicy.Details, result.State.PendingPlanningRequest is pending and 1 more.
        result.State.PlanningIntake.Should().Be(AgentIntakePolicy.Details);
        result.State.PendingPlanningRequest.Should().Be(pending);
        if (action == "out_of_scope") result.Reply.Should().EndWith(BudgetQuestionState().PendingQuestion);
        else result.Reply.Should().Be("Dạ bạn.");
    }

    // TEST-47: A turn timeout during the wording call still gives the dish; the caller's own cancellation is rethrown instead.
    /// <summary>Only the backend's deadline falls back; a client that left must not get a reply stored for it.</summary>
    [Theory]
    [InlineData(true)]
    public async Task RunAsync_SuggestionWordingCancelled_FallsBackOnlyOnTimeout(bool callerCancelled)
    {
        // STEP-01: Get context from IntakeContext and create source as CancellationTokenSource.
        var context = IntakeContext("gợi ý món cay dưới 60k", new AgentState());
        using var source = new CancellationTokenSource();
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync to return the tool call, then cancel when asked and throw.
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([SuggestCall()], null))
            .Returns(() => { if (callerCancelled) source.Cancel(); throw new OperationCanceledException(); });

        // STEP-03: Wrap Suggesting().RunAsync in act.
        var act = () => Suggesting(llm.Object).RunAsync(context, source.Token);

        // STEP-04: Assert act throws OperationCanceledException, or returns the fixed reply on a timeout.
        if (callerCancelled) await act.Should().ThrowAsync<OperationCanceledException>();
        else (await act()).Reply.Should().StartWith("Mình gợi ý Dish 1 ở Real restaurant");
    }

    // TEST-48: With no dish found the model words it, unless it names a dish nothing backs or the call fails; covers a plain sentence, an invented dish and a failure.
    /// <summary>The one case with no facts is where a model could offer a dish ANGI does not have.</summary>
    [Theory]
    [InlineData("Trên ANGI chưa có món nào hợp, bạn thử bỏ bớt điều kiện nhé.", true)]
    public async Task RunAsync_NothingFound_ShowsCheckedWordingOrFixedReply(string? reply, bool shown)
    {
        // STEP-01: Get context from IntakeContext and create second.
        var context = IntakeContext("gợi ý món cay dưới 60k", new AgentState());
        var second = reply is null ? AgentLlmStep.Failed(AgentLlmFailureKind.ProviderUnavailable, 500) : new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = reply });
        // STEP-02: Get llm from WordingLlm.
        var llm = WordingLlm(context, second);

        // STEP-03: Get result from Suggesting().RunAsync.
        var result = await Suggesting(llm.Object, empty: true, dishNames: Enumerable.Repeat("Phở Bò", 5).ToArray()).RunAsync(context, default);

        // STEP-04: Assert result.Reply is the expected value.
        result.Reply.Should().Be(shown ? reply : AgentDishSuggestionPolicy.NothingFound);
    }

    // TEST-49: A question about the user's own favourites is worded by the model; in an answer, a guess at a dish in lower case is replaced because an answer states facts of the plan.
    /// <summary>A social reply may mention food, so the prompt carries the rule there; an answer with no plan behind it still cannot name one.</summary>
    [Theory]
    [InlineData("chat", "Mình chưa biết món bạn thích nhất, để mình gợi ý theo khẩu vị của bạn nhé?", true)]
    public async Task RunAsync_OwnFavourite_AnswerNeverNamesAnInventedDish(string action, string reply, bool shown)
    {
        // STEP-01: Get context from IntakeContext and mock IAgentCatalogRepository.
        var context = IntakeContext("món ăn sở trường của tôi là gì?", new AgentState());
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.GetProvincesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["Hà Nội"]);
        catalog.Setup(x => x.GetDishNamesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Enumerable.Repeat("Phở Bò", 5).ToArray());
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = action, Reply = reply }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: catalog.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply contains or lacks the wording.
        if (shown) result.Reply.Should().Be(reply);
        else result.Reply.Should().NotContain("phở");
        // STEP-05: Verify GetDishNamesAsync is called only where the reply is checked against dish words.
        catalog.Verify(x => x.GetDishNamesAsync(It.IsAny<CancellationToken>()), action == "answer" ? Times.Once() : Times.Never());
    }

    // TEST-50: Over a plan even a request with no conditions goes to the model, leaves out the plan's dish, invites adding the suggestion, marks the offer and changes nothing else.
    /// <summary>"Vừa lập xong, gợi ý 1 món bất kì" used to repeat a dish of the plan and offer no way to add it.</summary>
    [Fact]
    public async Task RunAsync_PlainSuggestionOverPlan_InvitesAddingAnotherDish()
    {
        // STEP-01: Get context from a plan holding Dish 1 and create reply.
        var context = IntakeContext("gợi ý cho tôi 1 món bất kì", AgentTestData.State() with { NumDays = 2 }, AgentTestData.Snapshot(AgentTestData.Item() with { DishId = 1, DishName = "Dish 1" }));
        const string reply = "Bạn thử {name1} ở {restaurant1} nhé, khoảng {price1}. Muốn cho vào lịch thì nhắn mình ngày và bữa, ví dụ bữa tối ngày 2 nha.";
        // STEP-02: Get llm from WordingLlm.
        var llm = WordingLlm(context, new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = reply }));

        // STEP-03: Get result from Suggesting().RunAsync.
        var result = await Suggesting(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply names Dish 2 with the invitation, result.Snapshot is null and result.State.DishOffered is true.
        result.Reply.Should().Be("Bạn thử Dish 2 ở Real restaurant nhé, khoảng 50.000 đồng. Muốn cho vào lịch thì nhắn mình ngày và bữa, ví dụ bữa tối ngày 2 nha.");
        result.Snapshot.Should().BeNull();
        result.State.DishOffered.Should().BeTrue();
        // STEP-05: Verify RunAsync is called twice.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-51: A question from the model drops a guessed level without a repair, and a level the user stated is stored without its quote.
    /// <summary>A question costs no second call, and the quote must never reach stored state or a comparison of constraints.</summary>
    [Theory]
    [InlineData("answer", "cay nhẹ", true)]
    public async Task RunAsync_ModelStateWithTaste_KeepsOnlyStatedLevelsWithoutQuote(string action, string? quote, bool kept)
    {
        // STEP-01: Get context from IntakeContext with no plan to refresh and set the message.
        var context = IntakeContext("tôi thích cay nhẹ", AgentTestData.State());
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput
            { Action = action, Clarification = action == "clarify" ? "taste_scope" : null, Reply = "Bạn muốn áp dụng cho cả chuyến nhé?", State = context.State with { TasteTargets = [new("spicy", .25, quote)] } }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.State.TasteTargets holds the stated target with no quote, or nothing.
        if (kept) result.State.TasteTargets.Should().Equal(new AgentTasteTarget("spicy", .25));
        else result.State.TasteTargets.Should().BeEmpty();
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-52: A preview shows the model's sentence filled with the final plan's total; a sentence that fails its check falls back to the fixed summary and logs the replacement.
    /// <summary>Makes previews read naturally at no extra provider call, since the slots are filled after the backend's checks.</summary>
    [Theory]
    [InlineData("Lịch xong rồi nè, tổng khoảng {total}, bạn xem rồi chốt nhé!", true)]
    public async Task RunAsync_ProposalWording_IsFilledAfterChecks(string reply, bool shown)
    {
        // STEP-01: Get context from IntakeContext with a one-meal trip and a grant for its dish, and mock IAgentTelemetry.
        var state = AgentTestData.State() with { NumDays = 1, MealSlots = ["lunch"], BudgetAmount = 500000 };
        var context = IntakeContext("lập lịch đi", state);
        var telemetry = new Mock<IAgentTelemetry>();
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync to issue a grant and propose the dish.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            context.Grants.Clear();
            context.Grants.Add(new(1, "lunch", [1], Guid.NewGuid(), context.State));
            return Task.FromResult<AgentLlmStep?>(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = reply, State = context.State, Scope = new(), Items = [AgentTestData.Item(1, "a")] }))!;
        });

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, telemetry: telemetry.Object, catalog: [AgentTestData.Dish(1)]).RunAsync(context, default);

        // STEP-04: Assert result.Snapshot exists and result.Reply is the filled sentence or the fixed summary.
        result.Snapshot.Should().NotBeNull();
        if (shown) result.Reply.Should().StartWith("Lịch xong rồi nè, tổng khoảng").And.Contain("đồng/người, bạn xem rồi chốt nhé!").And.NotContain("{total}");
        else result.Reply.Should().StartWith("Xong rồi!").And.Contain("Mình đã lên lịch").And.NotContain("999.000");
        // STEP-05: Verify Record logs reply_replaced only when the wording failed.
        telemetry.Verify(x => x.Record("reply_replaced", context.ConversationId, context.TurnId, context.AttemptId, It.IsAny<object>()), shown ? Times.Never : Times.Once);
    }

    // TEST-53: A draft the builder cannot use gets one repair call with the reason; a usable repair is shown, a second unusable draft fails with the
    // controlled error, and the unfilled template reply is never returned (conversations 236 and 238).
    /// <summary>Keeps "{total}" and "{changes}" from reaching the user when a draft is rejected.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_UnusableDraft_RepairsOnceThenFailsControlled(bool recover)
    {
        // STEP-01: A one-meal trip with no source; the first output patches a source that does not exist.
        var state = AgentTestData.State() with { NumDays = 1, MealSlots = ["lunch"], BudgetAmount = 500000 };
        var context = IntakeContext("lập lịch đi", state);
        var unusable = new AgentOutput { Action = "patch_proposal", Reply = "Mình đổi {changes} nhé, tổng giờ khoảng {total}.", State = context.State, Scope = new(),
            Changes = [new("replace", "a", AgentTestData.Item(1, "a"))] };
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], unusable));
        llm.Setup(x => x.RunAsync(context, true, It.IsAny<CancellationToken>())).Returns(() =>
        {
            context.Grants.Clear();
            context.Grants.Add(new(1, "lunch", [1], Guid.NewGuid(), context.State));
            return Task.FromResult<AgentLlmStep?>(new AgentLlmStep([], recover
                ? new AgentOutput { Action = "create_proposal", Reply = "Lịch xong rồi nè, tổng khoảng {total}.", State = context.State, Scope = new(), Items = [AgentTestData.Item(1, "a")] }
                : unusable))!;
        });
        var run = () => Create(llm.Object, catalog: [AgentTestData.Dish(1)]).RunAsync(context, default);

        // STEP-02: Assert a usable repair is shown filled, and a second unusable draft throws ServiceUnavailableException.
        if (recover) (await run()).Reply.Should().NotContain("{total}").And.NotContain("{changes}");
        else await run.Should().ThrowAsync<ServiceUnavailableException>();
        // STEP-03: Verify exactly one repair call carried the rejection reason.
        llm.Verify(x => x.RunAsync(context, true, It.IsAny<CancellationToken>()), Times.Once);
        context.RepairFeedback.Should().NotBeNullOrEmpty();
    }

    // TEST-57: A dish no tool issued is sent back once with the reason; a corrected draft is shown, and one still made up after that is replaced by the dish issued for its meal.
    /// <summary>Hallucination guard (user decision 2026-11-12): a made-up dish never reaches the user, and the user is never asked anything about it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_MadeUpDish_RepairsOnceThenReplaces(bool recover)
    {
        // STEP-01: A one-meal trip whose only issued candidate is dish 1, while the model first picks dish 2.
        var state = AgentTestData.State() with { NumDays = 1, MealSlots = ["lunch"] };
        var context = IntakeContext("lập lịch đi", state);
        context.Grants.Add(new(1, "lunch", [1], Guid.NewGuid(), state));
        AgentOutput Draft(int dish) => new() { Action = "create_proposal", Reply = "Lịch xong rồi nè, tổng khoảng {total}.", State = context.State, Scope = new(), Items = [AgentTestData.Item(dish, "a")] };
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], Draft(2)));
        llm.Setup(x => x.RunAsync(context, true, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], Draft(recover ? 1 : 2)));

        // STEP-02: Run the turn.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2)]).RunAsync(context, default);

        // STEP-03: Assert one repair carried the reason, and either way the draft holds the issued dish, never the made-up one.
        llm.Verify(x => x.RunAsync(context, true, It.IsAny<CancellationToken>()), Times.Once);
        context.RepairFeedback.Should().Contain("Món 2");
        result.Snapshot!.Items.Select(item => item.DishId).Should().Equal(1);
    }

    // TEST-58: A dish kept from the saved plan that no longer passes (here gone from the catalog) is replaced by the dish issued for its meal, with no repair call;
    // with nothing issued for that meal it stays, so the plan never loses a meal.
    /// <summary>Conversation 570: after an itinerary change nine kept dishes no longer fit; they are neither shown as made up nor left out.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_StaleKeptDish_IsReplacedWhenAMealCandidateExists(bool issued)
    {
        // STEP-01: A saved one-meal plan holding dish 3, which the catalog no longer has, and a draft that keeps it.
        var state = AgentTestData.State() with { NumDays = 1, MealSlots = ["lunch"] };
        var kept = new AgentItem("a", 77, 1, "lunch", 0, 3);
        var context = AgentTestData.Context(new AgentSnapshot(state, [kept], []));
        context.State = state;
        if (issued) context.Grants.Add(new(1, "lunch", [1], Guid.NewGuid(), state));
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Lịch xong rồi nè, tổng khoảng {total}.", State = state, Scope = new(), Items = [kept] }));

        // STEP-02: Run the turn.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1)]).RunAsync(context, default);

        // STEP-03: Assert no repair call, and the kept dish was replaced only when a dish was issued for its meal.
        llm.Verify(x => x.RunAsync(context, true, It.IsAny<CancellationToken>()), Times.Never);
        result.Snapshot!.Items.Select(item => item.DishId).Should().Equal(issued ? 1 : 3);
    }

    // TEST-59: An edit that swaps in a made-up dish with nothing issued for that meal keeps the meal's dish from the saved plan instead of leaving it empty.
    /// <summary>The draft never shows the made-up dish, and a plan the user already has never loses a meal because of it.</summary>
    [Fact]
    public async Task RunAsync_MadeUpDishInEdit_KeepsSavedDish()
    {
        // STEP-01: A saved one-meal plan holding dish 1, no candidate issued this turn, and a draft that swaps in dish 9, which the catalog does not have.
        var state = AgentTestData.State() with { NumDays = 1, MealSlots = ["lunch"] };
        var context = AgentTestData.Context(new AgentSnapshot(state, [new AgentItem("a", 77, 1, "lunch", 0, 1)], []));
        context.State = state;
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Lịch xong rồi nè, tổng khoảng {total}.", State = state, Scope = new(), Items = [AgentTestData.Item(9, "a")] }));

        // STEP-02: Run the turn.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1)]).RunAsync(context, default);

        // STEP-03: Assert one repair was tried and the draft keeps the saved dish.
        llm.Verify(x => x.RunAsync(context, true, It.IsAny<CancellationToken>()), Times.Once);
        result.Snapshot!.Items.Select(item => item.DishId).Should().Equal(1);
    }

    // TEST-54: A new plan whose model output repeats an item key keeps every meal, giving the repeat a fresh key (conversation 238).
    /// <summary>An item key is only a technical identity, so a repeated one never discards the whole plan.</summary>
    [Fact]
    public void Build_RepeatedItemKey_GetsFreshKey()
    {
        // STEP-01: A new two-meal plan whose lunch and dinner share the key "a".
        var context = AgentTestData.Context();
        var result = new ANGI.Application.Services.Agent.Proposals.AgentProposalBuilder().Build(context, new AgentOutput { Action = "create_proposal", Reply = "x", Scope = new(),
            Items = [AgentTestData.Item(1, "a", 1, "lunch"), AgentTestData.Item(2, "a", 1, "dinner")] });

        // STEP-02: Assert both meals are kept with distinct keys and the first keeps its key.
        result.Items.Should().HaveCount(2);
        result.Items.Select(item => item.ItemKey).Should().OnlyHaveUniqueItems().And.Contain("a");
    }

    // TEST-55: An empty reply is sent back once for the model to write; a written repair is shown as the model wrote it, a second empty reply fails
    // with the controlled error, and the backend never puts its own sentence in its place (conversation 239).
    /// <summary>Keeps every chat reply in the model's words without ever showing an empty message.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_EmptyReply_RepairsOnceThenFailsControlled(bool recover)
    {
        // STEP-01: The model answers a chat message with an empty reply, then with a written one only if recover.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "chat", Reply = "" }));
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "chat", Reply = recover ? "Chào bạn nhé!" : " " }));
        var context = AgentTestData.Context();
        var run = () => Create(llm.Object).RunAsync(context, default);

        // STEP-02: Assert the written repair is shown as written, and a second empty reply throws ServiceUnavailableException.
        if (recover) (await run()).Reply.Should().Be("Chào bạn nhé!");
        else await run.Should().ThrowAsync<ServiceUnavailableException>();
        // STEP-03: Verify exactly one repair call.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Returns an orchestrator over a catalog of dishes and no ranking, for suggestion turns.</summary>
    private static AgentOrchestrator Suggesting(IAgentLlmService llm, bool intake = false, AgentLimits? limits = null, IAgentTelemetry? telemetry = null, string[]? dishNames = null, bool empty = false)
    {
        var catalog = Enumerable.Range(1, 3).Select(id => AgentTestData.Dish(id)).ToArray();
        var fixture = AgentTestData.Validator(catalog);
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<ANGI.Application.Common.Models.Discovery.DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(empty ? [] : catalog.Select(dish => dish.Id).ToArray());
        var policy = new AgentConstraintGuard();
        limits ??= new AgentLimits();
        telemetry ??= Mock.Of<IAgentTelemetry>();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), policy, new AgentRouteEstimator(fixture.Routes.Object), limits, telemetry);
        var names = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        names.Setup(x => x.GetDishNamesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(dishNames ?? []);
        return new(llm, tools, policy, new(), fixture.Validator, limits, telemetry, intake ? IntakeCatalog() : dishNames is null ? null : names.Object);
    }

    /// <summary>Builds the suggest_dish call a model makes for a condition it read.</summary>
    private static AgentToolCall SuggestCall() =>
        new("s", "suggest_dish", System.Text.Json.JsonSerializer.SerializeToElement(new AgentSuggestionInput(TagIds: [], ExcludeTagIds: [], MaxPrice: 60000, TasteTargets: []), AgentJson.Options));

    /// <summary>Returns a model that calls suggest_dish, then answers the wording call with the given step.</summary>
    private static Mock<IAgentLlmService> WordingLlm(AgentContext context, AgentLlmStep second)
    {
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([SuggestCall()], null)).ReturnsAsync(second);
        return llm;
    }

    /// <summary>Builds the plan of conversation 121: day 1 wrongly located in Hà Đông and day 2 at Lăng Bác.</summary>
    private static AgentContext WrongDayOneContext(string content)
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.State = context.State with { Province = "Hà Nội", NumDays = 3,
            DayAreas = [new(1, "Hồ Gươm", 20.978894m, 105.785555m, 3m, "Hà Đông"), new(2, "Lăng Bác", 21.0368m, 105.8347m, 3m, "Ba Đình")] };
        context.Source = context.Source! with { State = context.State };
        context.Request.Content = content;
        return context;
    }

    /// <summary>Returns a resolver over Hà Nội districts with enough restaurants each, and no map service.</summary>
    private static AgentPlaceResolver Places()
    {
        var addresses = new[] { "Hoàn Kiếm", "Ba Đình", "Hoàng Mai", "Cầu Giấy" }.SelectMany((district, index) => Enumerable.Range(0, 25)
            .Select(_ => new AgentCatalogPlace("Hà Nội", $"1 Phố Hai Bà Trưng, P. Cửa Nam, {district}, Hà Nội", 21.03m + index / 100m, 105.85m)));
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.GetPublicPlacesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(addresses.ToArray());
        return new(catalog.Object);
    }

    /// <summary>Builds a turn with the given message over the given state.</summary>
    private static AgentContext IntakeContext(string content, AgentState state, AgentSnapshot? source = null)
    {
        var context = AgentTestData.Context(source);
        context.State = state;
        context.Request.Content = content;
        return context;
    }

    /// <summary>Returns the state right after the backend asked for the budget of a known trip with no plan yet.</summary>
    private static AgentState BudgetQuestionState() => new() { Province = "Hà Nội", NumDays = 3, PlanningIntake = AgentIntakePolicy.Details,
        PendingQuestion = "Bạn cho mình biết ngân sách cho một người (tổng chuyến hoặc mỗi ngày) để lập lịch nhé?" };

    /// <summary>Returns a catalog covering only Hà Nội, like the current local data, with a fixed nearest-province answer.</summary>
    private static ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository IntakeCatalog(string? nearest = "Hà Nội")
    {
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.GetProvincesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["Hà Nội"]);
        catalog.Setup(x => x.NearestProvinceAsync(It.IsAny<decimal>(), It.IsAny<decimal>(), AgentIntakePolicy.MaxNearbyKm, It.IsAny<CancellationToken>())).ReturnsAsync(nearest);
        return catalog.Object;
    }

    /// <summary>Returns a model that gives these steps in order, the way the provider would over the turns or calls of a test.</summary>
    private static Mock<IAgentLlmService> Scripted(params AgentLlmStep[] steps)
    {
        var llm = new Mock<IAgentLlmService>();
        var sequence = llm.SetupSequence(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()));
        foreach (var step in steps) sequence = sequence.ReturnsAsync(step);
        return llm;
    }

    /// <summary>Builds the model's question of one kind, carrying the state it read.</summary>
    private static AgentLlmStep Asks(string clarification, string reply, AgentState? state = null) =>
        new([], new AgentOutput { Action = "clarify", Clarification = clarification, Reply = reply, State = state });

    /// <summary>Builds the locate_places call a model makes for an itinerary or an area it read.</summary>
    private static AgentLlmStep Locates(string? tripArea, params AgentLocateStop[] stops) =>
        new([new AgentToolCall("l", "locate_places", System.Text.Json.JsonSerializer.SerializeToElement(new AgentLocateInput(tripArea, stops), AgentJson.Options))], null);

    private static AgentOrchestrator Create(IAgentLlmService llm, AgentLimits? configuredLimits = null, IAgentTelemetry? telemetry = null,
        ANGI.Domain.Entities.Dish[]? catalog = null, ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository? catalogSearch = null,
        ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository? intakeCatalog = null, AgentPlaceResolver? places = null)
    {
        var fixture = AgentTestData.Validator(catalog ?? [AgentTestData.Dish()]);
        telemetry ??= Mock.Of<IAgentTelemetry>(); var limits = configuredLimits ?? new AgentLimits(); var state = new AgentConstraintGuard();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), state,
            new AgentRouteEstimator(fixture.Routes.Object), limits, telemetry, catalogSearch: catalogSearch ?? intakeCatalog, places: places);
        return new(llm, tools, state, new(), fixture.Validator, limits, telemetry, intakeCatalog, places);
    }
}
