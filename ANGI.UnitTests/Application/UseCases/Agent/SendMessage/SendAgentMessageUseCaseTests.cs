using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent;
using ANGI.Application.UseCases.Agent.SendMessage;
using ANGI.Application.UseCases.Validators.Agent;
using ANGI.UnitTests.Application.Services.Agent;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Agent.SendMessage;

/// <summary>Verifies idempotent turn admission and isolated failure recovery.</summary>
public sealed class SendAgentMessageUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_ReviewExistingProposal_ReturnsFullPreviewWithoutProviderOrCommit()
    {
        var fixture = new AgentUseCaseFixture();
        fixture.Conversation.CurrentProposalId = 9;
        var proposal = ReadyProposal(9, AgentTestData.Snapshot(AgentTestData.Item(), AgentTestData.Item(1, "b", 2)));
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        var request = Request(); request.Content = "hãy lập lịch giúp tôi để chốt lịch";
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);
        result.Proposal!.Snapshot.Items.Should().HaveCount(2); result.Commit.Should().BeNull();
        fixture.Llm.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_AcceptConstraintChange_GeneratesAndPersistsFreshCompletePreview()
    {
        var fixture = new AgentUseCaseFixture(); var previousState = AgentTestData.State();
        fixture.Conversation.CurrentProposalId = 9;
        fixture.Conversation.State = AgentJson.Write(previousState with { PendingQuestion = "Tăng ngân sách?",
            PendingStateChange = previousState with { BudgetAmount = 700000 } });
        var proposal = ReadyProposal(9, AgentTestData.Snapshot(AgentTestData.Item(), AgentTestData.Item(1, "b", 2)));
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.ReserveProposalIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(10);
        fixture.Repository.Setup(repo => repo.NextProposalVersionAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(2);
        fixture.Llm.Setup(llm => llm.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "patch_proposal", Reply = "Updated", BaseProposalVersion = 1, State = previousState,
                Changes = [new("move", "a", AgentTestData.Item() with { MealSlot = "breakfast" })] }));
        var request = Request(); request.Content = "xác nhận thay đổi";
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);
        result.Proposal!.ProposalId.Should().Be(10); result.Proposal.Snapshot.Items.Should().HaveCount(2);
        result.Proposal.Snapshot.State.BudgetAmount.Should().Be(700000);
        proposal.Status.Should().Be(ANGI.Domain.Enums.AgentProposalStatus.Superseded);
        result.Commit.Should().BeNull();
    }

    private static ANGI.Domain.Entities.AgentProposal ReadyProposal(long id, AgentSnapshot snapshot) => new()
    {
        Id = id, ConversationId = 7, ProposalVersion = 1, Status = ANGI.Domain.Enums.AgentProposalStatus.Ready,
        ExpiresAt = AgentUseCaseFixture.Now.AddDays(1), Payload = AgentJson.Write(snapshot),
        ValidationResult = AgentJson.Write(new AgentValidation([], [], 50000, []))
    };

    [Fact]
    public async Task ExecuteAsync_ChangedStateWithoutFreshPreview_DoesNotReplayOldCommitAsNewSave()
    {
        var fixture = new AgentUseCaseFixture(); fixture.Conversation.CurrentProposalId = 9;
        fixture.Conversation.State = AgentJson.Write(AgentTestData.State() with { Note = "Thích cay, ít ngọt" });
        var proposal = ReadyProposal(9, AgentTestData.Snapshot(AgentTestData.Item()));
        proposal.Status = ANGI.Domain.Enums.AgentProposalStatus.Committed;
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        var request = Request(); request.Content = "chốt lịch này";
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);
        result.Commit.Should().BeNull(); result.Reply.Should().Contain("chưa có đề xuất mới");
        fixture.Llm.VerifyNoOtherCalls(); fixture.Roadmaps.VerifyNoOtherCalls();
    }

    // TEST-01: A completed stale-version retry replays its stored response without provider calls or extra saves.
    /// <summary>Checks replay-before-version validation using the real request hash implementation.</summary>
    [Fact]
    public async Task ExecuteAsync_CompletedRetry_ReplaysWithoutProviderOrSave()
    {
        var fixture = new AgentUseCaseFixture(); var request = Request(); var useCase = Create(fixture);
        var first = await useCase.ExecuteAsync(7, request, CancellationToken.None);
        var replay = await useCase.ExecuteAsync(7, request, CancellationToken.None);
        replay.Replayed.Should().BeTrue(); replay.ConversationVersion.Should().Be(first.ConversationVersion);
        fixture.Llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        request.SelectedItemKey = "different-business-choice";
        (await ((Func<Task>)(async () => await useCase.ExecuteAsync(7, request, CancellationToken.None))).Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("MESSAGE_ID_REUSED");
    }

    // TEST-02: Busy, stale and expired conversations reject admission before the LLM.
    /// <summary>Checks all pre-execution conversation guards at their boundary.</summary>
    [Theory]
    [InlineData("busy", "CONVERSATION_BUSY")][InlineData("version", "CONVERSATION_VERSION_CONFLICT")][InlineData("expired", "CONVERSATION_EXPIRED")]
    public async Task ExecuteAsync_ConversationGuard_RejectsBeforeLlm(string area, string code)
    {
        var fixture = new AgentUseCaseFixture();
        if (area == "busy") { fixture.Conversation.ProcessingTurnId = 1; fixture.Conversation.ProcessingAttemptId = 2; fixture.Conversation.ProcessingUntil = AgentUseCaseFixture.Now.AddSeconds(1); }
        if (area == "version") fixture.Conversation.Version = 2;
        if (area == "expired") fixture.Conversation.ExpiresAt = AgentUseCaseFixture.Now;
        var act = () => Create(fixture).ExecuteAsync(7, Request(), CancellationToken.None);
        (await act.Should().ThrowAsync<AppException>()).Which.ErrorCode.Should().Be(code);
        fixture.Llm.VerifyNoOtherCalls();
    }

    // TEST-03: Persistent minute and daily quota exhaustion returns the correct retry delay without provider calls.
    /// <summary>Verifies both independently configured per-user cost limits.</summary>
    [Theory]
    [InlineData(false, 60)][InlineData(true, 86400)]
    public async Task ExecuteAsync_QuotaExceeded_RejectsBeforeLlm(bool daily, int retryAfter)
    {
        var fixture = new AgentUseCaseFixture();
        fixture.Repository.Setup(x => x.CountAttemptsAsync(3, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, DateTime since, CancellationToken _) => daily ? (since == AgentUseCaseFixture.Now.AddDays(-1) ? 100 : 0) : 10);
        var act = () => Create(fixture).ExecuteAsync(7, Request(), CancellationToken.None);
        (await act.Should().ThrowAsync<AgentRateLimitedException>()).Which.RetryAfterSeconds.Should().Be(retryAfter);
        fixture.Llm.VerifyNoOtherCalls();
    }

    // TEST-04: Provider failure resets rolled-back tracking before releasing only its own lease.
    /// <summary>Prevents a recovery flush from persisting a partially prepared proposal.</summary>
    [Fact]
    public async Task ExecuteAsync_ProviderFailure_ReleasesLeaseWithCleanTracking()
    {
        var fixture = new AgentUseCaseFixture();
        fixture.Llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync((AgentLlmStep?)null);
        await ((Func<Task>)(async () => await Create(fixture).ExecuteAsync(7, Request(), CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();
        fixture.Repository.Verify(x => x.ResetTracking(), Times.Once);
        fixture.Conversation.ProcessingAttemptId.Should().BeNull(); fixture.Message!.ProcessingStatus.Should().Be(ANGI.Domain.Enums.AgentProcessingStatus.Failed);
    }

    // TEST-05: Equivalent decimal scales and province casing do not turn a completed business retry into a different message.
    /// <summary>Checks canonical hashing ignores representation differences while retaining business identity.</summary>
    [Fact]
    public async Task ExecuteAsync_EquivalentStateRepresentation_Replays()
    {
        var fixture = new AgentUseCaseFixture(); var request = Request();
        request.ConfirmedState = new AgentState { Province = "Da Nang", NumDays = 2, BudgetAmount = 500000.0m };
        var useCase = Create(fixture); await useCase.ExecuteAsync(7, request, CancellationToken.None);
        request.ConfirmedState = request.ConfirmedState with { Province = "DA NANG", BudgetAmount = 500000m };
        (await useCase.ExecuteAsync(7, request, CancellationToken.None)).Replayed.Should().BeTrue();
    }

    // TEST-06: Explicit save without a preview produces a natural answer without calling the provider or creating a roadmap.
    /// <summary>Supports chat-only saving without asking the client to construct a confirm request.</summary>
    [Fact]
    public async Task ExecuteAsync_SaveWithoutPreview_AnswersWithoutLlm()
    {
        var fixture = new AgentUseCaseFixture(); var request = Request(); request.Content = "lưu lịch trình này";
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);
        result.Commit.Should().BeNull(); result.Reply.Should().Contain("Chưa có");
        fixture.Llm.VerifyNoOtherCalls(); fixture.Roadmaps.VerifyNoOtherCalls();
    }

    // TEST-07: A short affirmative accepts only the pending constraint snapshot and never saves a roadmap.
    /// <summary>Checks both acceptance and rejection without trusting model-generated consent.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_PendingChange_RequiresNextTurnConsent(bool accept)
    {
        var fixture = new AgentUseCaseFixture();
        var state = AgentTestData.State();
        fixture.Conversation.State = AgentJson.Write(state with { PendingQuestion = "Tăng ngân sách?", PendingStateChange = state with { BudgetAmount = 700000 } });
        var request = Request(); request.Content = accept ? "đồng ý" : "giữ nguyên";
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);
        var saved = AgentJson.Read<AgentState>(fixture.Conversation.State);
        saved.BudgetAmount.Should().Be(accept ? 700000 : 500000); saved.PendingStateChange.Should().BeNull(); saved.PendingQuestion.Should().BeNull();
        result.Commit.Should().BeNull();
        fixture.Llm.Verify(x => x.RunAsync(It.Is<AgentContext>(context => context.ReplanAfterConfirmation && context.State.BudgetAmount == 700000), false, It.IsAny<CancellationToken>()),
            accept ? Times.Once() : Times.Never());
        fixture.Roadmaps.VerifyNoOtherCalls();
    }

    // TEST-08: Ordinary or negated chat is sent to the model and cannot reach the deterministic save writer.
    /// <summary>Protects against ambiguous agreement and negative save instructions.</summary>
    [Theory]
    [InlineData("đồng ý")]
    [InlineData("đừng lưu lịch trình này")]
    public async Task ExecuteAsync_NotExplicitSave_NeverCommits(string content)
    {
        var fixture = new AgentUseCaseFixture(); var request = Request(); request.Content = content;
        (await Create(fixture).ExecuteAsync(7, request, CancellationToken.None)).Commit.Should().BeNull();
        fixture.Llm.Verify(llm => llm.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Roadmaps.VerifyNoOtherCalls();
    }

    // TEST-09: Repeated prompt-only text creates distinct messages instead of replaying or conflicting with an earlier turn.
    /// <summary>Checks automatic allocation, a changed revision and retrying the returned ID without provider usage.</summary>
    [Fact]
    public async Task ExecuteAsync_PromptOnly_AllocatesFreshIdentityAndCanReplay()
    {
        var fixture = new AgentUseCaseFixture(); var useCase = Create(fixture);
        var request = new SendAgentMessageRequestDto { Content = "bỏ bản nháp" };
        var first = await useCase.ExecuteAsync(7, request, CancellationToken.None);
        var second = await useCase.ExecuteAsync(7, request, CancellationToken.None);
        second.ClientMessageId.Should().BeGreaterThan(first.ClientMessageId!.Value);
        second.Replayed.Should().BeFalse(); second.ConversationVersion.Should().BeGreaterThan(first.ConversationVersion);
        request.ClientMessageId.Should().BeNull(); request.ExpectedConversationVersion.Should().BeNull();
        request.ClientMessageId = second.ClientMessageId;
        var replay = await useCase.ExecuteAsync(7, request, CancellationToken.None);
        replay.Replayed.Should().BeTrue(); replay.ClientMessageId.Should().Be(second.ClientMessageId);
        fixture.Llm.VerifyNoOtherCalls();
    }

    // TEST-10: Supplying a stale version remains an explicit conflict even without a client-selected message ID.
    /// <summary>Checks that prompt-only defaults do not silently ignore requested concurrency protection.</summary>
    [Fact]
    public async Task ExecuteAsync_OptionalIdWithExplicitStaleVersion_StillConflicts()
    {
        var fixture = new AgentUseCaseFixture(); fixture.Conversation.Version = 3;
        var act = () => Create(fixture).ExecuteAsync(7, new() { Content = "bỏ bản nháp", ExpectedConversationVersion = 1 }, CancellationToken.None);
        (await act.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("CONVERSATION_VERSION_CONFLICT");
        fixture.Llm.VerifyNoOtherCalls();
    }

    // TEST-11: Prompt-only admission retains the busy, expiry and quota guards before provider execution.
    /// <summary>Checks that omission of transport metadata cannot bypass existing admission limits.</summary>
    [Theory]
    [InlineData("busy", "CONVERSATION_BUSY")]
    [InlineData("expired", "CONVERSATION_EXPIRED")]
    [InlineData("quota", "AGENT_RATE_LIMITED")]
    public async Task ExecuteAsync_PromptOnly_StillEnforcesAdmissionGuards(string guard, string code)
    {
        var fixture = new AgentUseCaseFixture();
        if (guard == "busy") { fixture.Conversation.ProcessingTurnId = 1; fixture.Conversation.ProcessingAttemptId = 2; fixture.Conversation.ProcessingUntil = AgentUseCaseFixture.Now.AddSeconds(30); }
        if (guard == "expired") fixture.Conversation.ExpiresAt = AgentUseCaseFixture.Now;
        if (guard == "quota") fixture.Repository.Setup(x => x.CountAttemptsAsync(3, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(100);
        var act = () => Create(fixture).ExecuteAsync(7, new() { Content = "Tôi muốn ăn ở Việt Nam" }, CancellationToken.None);
        (await act.Should().ThrowAsync<AppException>()).Which.ErrorCode.Should().Be(code);
        fixture.Llm.VerifyNoOtherCalls();
    }

    /// <summary>Creates a complete real use case over isolated persistence/provider boundaries.</summary>
    private static SendAgentMessageUseCase Create(AgentUseCaseFixture fixture) => new(new SendAgentMessageRequestDtoValidator(fixture.Limits),
        fixture.Repository.Object, fixture.Roadmaps.Object, fixture.User.Object, fixture.UnitOfWork.Object, fixture.Clock, fixture.Limits,
        new AgentContextBuilder(fixture.Repository.Object, fixture.Roadmaps.Object, fixture.Limits, fixture.Clock, AgentTestData.Validator().Tags.Object), fixture.Orchestrator(), fixture.Telemetry, fixture.Committer());
    /// <summary>Creates the minimal valid logical user request.</summary>
    private static SendAgentMessageRequestDto Request() => new() { ClientMessageId = 1, ExpectedConversationVersion = 1, Content = "Explain the proposal" };
}
