using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Proposals;
using ANGI.Application.UseCases.Agent.SendMessage;
using ANGI.Application.UseCases.Validators.Agent;
using ANGI.UnitTests.Application.Services.Agent;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Agent.SendMessage;

/// <summary>Verifies idempotent turn admission and isolated failure recovery.</summary>
public sealed class SendAgentMessageUseCaseTests
{
    // TEST-01: Incomplete planning does not return the old fallback proposal as the current result.
    /// <summary>A question already shows nothing changed and the card stays on screen, so the extra line only makes the reply read like a system notice.</summary>
    [Fact]
    public async Task ExecuteAsync_ClarifyingQuestion_AddsNoKeptDraftLine()
    {
        // STEP-01: Create fixture with a ready proposal and a model question.
        var fixture = new AgentUseCaseFixture();
        fixture.Conversation.CurrentProposalId = 9;
        var proposal = ReadyProposal(9, AgentTestData.Snapshot(AgentTestData.Item()));
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Llm.Setup(llm => llm.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "clarify", Clarification = "edit_scope", Reply = "Bạn muốn đổi món của ngày nào nhỉ?" }));
        var request = Request();
        request.Content = "đổi cái đó đi";

        // STEP-02: Run useCase.ExecuteAsync with request and keep result.
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);

        // STEP-03: Assert the reply is the question alone.
        result.Reply.Should().Be("Bạn muốn đổi món của ngày nào nhỉ?");
    }

    /// <summary>Prevents the user from seeing an outdated proposal as the answer to an unfinished request.</summary>
    [Fact]
    public async Task ExecuteAsync_IncompletePlanning_DoesNotReturnOldFallbackAsCurrentResult()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, set fixture.Conversation.CurrentProposalId to 9 and set fixture.Limits.MaxLlmCalls to 1.
        var fixture = new AgentUseCaseFixture();
        fixture.Conversation.CurrentProposalId = 9;
        fixture.Limits.MaxLlmCalls = 1;
        // STEP-02: Read proposal through ReadyProposal.
        var proposal = ReadyProposal(9, AgentTestData.Snapshot(AgentTestData.Item()));
        // STEP-03: Stub fixture.GetProposalSnapshotAsync, fixture.GetProposalAsync and fixture.RunAsync.
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Llm.Setup(llm => llm.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "clarify", Clarification = "coverage", Reply = "Thiếu món" }));
        // STEP-04: Get request from Request and set request.Content to a test text.
        var request = Request();
        request.Content = "tôi muốn ngày 1 ăn đồ ngọt vừa, ngày 2 chỉ có đồ uống thôi, ngày 3 thì theo bạn quyết định";
        // STEP-05: Build useCase under test through Create.
        var useCase = Create(fixture);

        // STEP-06: Run useCase.ExecuteAsync with request and keep result.
        var result = await useCase.ExecuteAsync(7, request, CancellationToken.None);
        // STEP-07: Run useCase.ExecuteAsync with request and keep replay.
        var replay = await useCase.ExecuteAsync(7, request, CancellationToken.None);

        // STEP-08: Assert result.Proposal is null and fixture.Conversation.CurrentProposalId is 9.
        result.Proposal.Should().BeNull();
        fixture.Conversation.CurrentProposalId.Should().Be(9);
        // STEP-09: Assert proposal.Status is Enums.AgentProposalStatus.Ready and result.Reply contains the expected text.
        proposal.Status.Should().Be(ANGI.Domain.Enums.AgentProposalStatus.Ready);
        result.Reply.Should().Contain("chưa hoàn tất việc sắp xếp lịch").And.Contain("Bản đề xuất trước đó được giữ lại");
        // STEP-10: Assert replay.Reply is result.Reply, replay.Proposal is null and replay.Replayed is true.
        replay.Reply.Should().Be(result.Reply);
        replay.Proposal.Should().BeNull();
        replay.Replayed.Should().BeTrue();
        // STEP-11: Verify AddProposal is never called and RunAsync is called once.
        fixture.Repository.Verify(repo => repo.AddProposal(It.IsAny<ANGI.Domain.Entities.AgentProposal>()), Times.Never);
        fixture.Llm.Verify(llm => llm.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Reviewing the existing proposal returns its full preview without calling the provider or committing.
    /// <summary>Ensures reviewing a plan is free of model cost and never saves it.</summary>
    [Fact]
    public async Task ExecuteAsync_ReviewExistingProposal_ReturnsFullPreviewWithoutProviderOrCommit()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture and set fixture.Conversation.CurrentProposalId to 9.
        var fixture = new AgentUseCaseFixture();
        fixture.Conversation.CurrentProposalId = 9;
        // STEP-02: Read proposal through ReadyProposal.
        var proposal = ReadyProposal(9, AgentTestData.Snapshot(AgentTestData.Item(), AgentTestData.Item(1, "b", 2)));
        // STEP-03: Stub fixture.GetProposalSnapshotAsync and fixture.GetProposalAsync.
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        // STEP-04: Get request from Request and set request.Content to a test text.
        var request = Request(); request.Content = "hãy lập lịch giúp tôi để chốt lịch";
        // STEP-05: Run Create().ExecuteAsync with request and keep result.
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);
        // STEP-06: Assert result.Proposal.Snapshot.Items has 2 item(s) and result.Commit is null.
        result.Proposal!.Snapshot.Items.Should().HaveCount(2); result.Commit.Should().BeNull();
        // STEP-07: Verify fixture receives no other calls.
        fixture.Llm.VerifyNoOtherCalls();
    }

    // TEST-03: Accepting a pending constraint change generates and persists a fresh complete preview.
    /// <summary>Ensures an accepted constraint change produces a new full preview instead of reusing the old one.</summary>
    [Fact]
    public async Task ExecuteAsync_AcceptConstraintChange_GeneratesAndPersistsFreshCompletePreview()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, get previousState from AgentTestData.State, set fixture.Conversation.CurrentProposalId to 9 and 1 more.
        var fixture = new AgentUseCaseFixture(); var previousState = AgentTestData.State();
        fixture.Conversation.CurrentProposalId = 9;
        fixture.Conversation.State = AgentJson.Write(previousState with { PendingQuestion = "Tăng ngân sách?",
            PendingStateChange = previousState with { BudgetAmount = 700000 } });
        // STEP-02: Read proposal through ReadyProposal.
        var proposal = ReadyProposal(9, AgentTestData.Snapshot(AgentTestData.Item(), AgentTestData.Item(1, "b", 2)));
        // STEP-03: Stub fixture.GetProposalSnapshotAsync, fixture.GetProposalAsync, fixture.ReserveProposalIdAsync, fixture.NextProposalVersionAsync and 1 more.
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.ReserveProposalIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(10);
        fixture.Repository.Setup(repo => repo.NextProposalVersionAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(2);
        fixture.Llm.SetupSequence(llm => llm.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(Answered("accept")).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "patch_proposal", Reply = "Updated", BaseProposalVersion = 1, State = previousState,
                Changes = [new("move", "a", AgentTestData.Item() with { MealSlot = "breakfast" })] }));
        // STEP-04: Get request from Request and set request.Content to a test text.
        var request = Request(); request.Content = "quất luôn bạn ơi";
        // STEP-05: Run Create().ExecuteAsync with request and keep result.
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);
        // STEP-06: Assert result.Proposal.ProposalId is 10, result.Proposal.Snapshot.Items has 2 item(s) and 3 more.
        result.Proposal!.ProposalId.Should().Be(10); result.Proposal.Snapshot.Items.Should().HaveCount(2);
        result.Proposal.Snapshot.State.BudgetAmount.Should().Be(700000);
        proposal.Status.Should().Be(ANGI.Domain.Enums.AgentProposalStatus.Superseded);
        result.Commit.Should().BeNull();
    }

    /// <summary>A model turn that only reports how it read the reply to the open question.</summary>
    private static AgentLlmStep Answered(string reading) => new([], new AgentOutput { Action = "answer", Reply = "", PendingAnswer = reading });

    /// <summary>Creates a ready, unexpired proposal entity for conversation 7 that wraps the given snapshot.</summary>
    private static ANGI.Domain.Entities.AgentProposal ReadyProposal(long id, AgentSnapshot snapshot) => new()
    {
        Id = id, ConversationId = 7, ProposalVersion = 1, Status = ANGI.Domain.Enums.AgentProposalStatus.Ready,
        ExpiresAt = AgentUseCaseFixture.Now.AddDays(1), Payload = AgentJson.Write(snapshot),
        ValidationResult = AgentJson.Write(new AgentValidation([], [], 50000, []))
    };

    // TEST-04: A changed state without a fresh preview does not replay the old commit as a new save.
    /// <summary>Prevents an old commit result from being reported as a save of the changed state.</summary>
    [Fact]
    public async Task ExecuteAsync_ChangedStateWithoutFreshPreview_DoesNotReplayOldCommitAsNewSave()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, set fixture.Conversation.CurrentProposalId to 9 and set fixture.Conversation.State to AgentJson.Write().
        var fixture = new AgentUseCaseFixture(); fixture.Conversation.CurrentProposalId = 9;
        fixture.Conversation.State = AgentJson.Write(AgentTestData.State() with { Note = "Thích cay, ít ngọt" });
        // STEP-02: Read proposal through ReadyProposal.
        var proposal = ReadyProposal(9, AgentTestData.Snapshot(AgentTestData.Item()));
        // STEP-03: Set proposal.Status to Enums.AgentProposalStatus.Committed.
        proposal.Status = ANGI.Domain.Enums.AgentProposalStatus.Committed;
        // STEP-04: Stub fixture.GetProposalSnapshotAsync and fixture.GetProposalAsync.
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        // STEP-05: Get request from Request and set request.Content to a test text.
        var request = Request(); request.Content = "chốt lịch này";
        // STEP-06: Run Create().ExecuteAsync with request and keep result.
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);
        // STEP-07: Assert result.Commit is null and result.Reply contains the expected text.
        result.Commit.Should().BeNull(); result.Reply.Should().Contain("chưa có lịch mới");
        // STEP-08: Verify fixture receives no other calls.
        fixture.Llm.VerifyNoOtherCalls(); fixture.Roadmaps.VerifyNoOtherCalls();
    }

    // TEST-05: A completed stale-version retry replays its stored response without provider calls or extra saves.
    /// <summary>Checks replay-before-version validation using the real request hash implementation.</summary>
    [Fact]
    public async Task ExecuteAsync_CompletedRetry_ReplaysWithoutProviderOrSave()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture and get request from Request.
        // STEP-02: Build useCase under test through Create.
        var fixture = new AgentUseCaseFixture(); var request = Request(); var useCase = Create(fixture);
        // STEP-03: Run useCase.ExecuteAsync with request and keep first.
        var first = await useCase.ExecuteAsync(7, request, CancellationToken.None);
        // STEP-04: Run useCase.ExecuteAsync with request and keep replay.
        var replay = await useCase.ExecuteAsync(7, request, CancellationToken.None);
        // STEP-05: Assert replay.Replayed is true and replay.ConversationVersion is first.ConversationVersion.
        replay.Replayed.Should().BeTrue(); replay.ConversationVersion.Should().Be(first.ConversationVersion);
        // STEP-06: Verify RunAsync is called once and SaveChangesAsync is called 2 times.
        fixture.Llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        // STEP-07: Set request.SelectedItemKey to "different-business-choice".
        request.SelectedItemKey = "different-business-choice";
        // STEP-08: Assert useCase.ExecuteAsync() throws ConflictException and the thrown exception.ErrorCode is "MESSAGE_ID_REUSED".
        (await ((Func<Task>)(async () => await useCase.ExecuteAsync(7, request, CancellationToken.None))).Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("MESSAGE_ID_REUSED");
    }

    // TEST-06: Busy, stale and expired conversations reject admission before the LLM.
    /// <summary>Checks all pre-execution conversation guards at their boundary.</summary>
    [Theory]
    [InlineData("busy", "CONVERSATION_BUSY")][InlineData("version", "CONVERSATION_VERSION_CONFLICT")][InlineData("expired", "CONVERSATION_EXPIRED")]
    public async Task ExecuteAsync_ConversationGuard_RejectsBeforeLlm(string area, string code)
    {
        // STEP-01: Create fixture as AgentUseCaseFixture and 3 more.
        var fixture = new AgentUseCaseFixture();
        if (area == "busy") { fixture.Conversation.ProcessingTurnId = 1; fixture.Conversation.ProcessingAttemptId = 2; fixture.Conversation.ProcessingUntil = AgentUseCaseFixture.Now.AddSeconds(1); }
        if (area == "version") fixture.Conversation.Version = 2;
        if (area == "expired") fixture.Conversation.ExpiresAt = AgentUseCaseFixture.Now;
        // STEP-02: Wrap Create().ExecuteAsync in act so its outcome can be asserted.
        var act = () => Create(fixture).ExecuteAsync(7, Request(), CancellationToken.None);
        // STEP-03: Assert act throws AppException and the thrown exception.ErrorCode is code.
        (await act.Should().ThrowAsync<AppException>()).Which.ErrorCode.Should().Be(code);
        // STEP-04: Verify fixture receives no other calls.
        fixture.Llm.VerifyNoOtherCalls();
    }

    // TEST-07: Persistent minute and daily quota exhaustion returns the correct retry delay without provider calls.
    /// <summary>Verifies both independently configured per-user cost limits.</summary>
    [Theory]
    [InlineData(false, 60)][InlineData(true, 86400)]
    public async Task ExecuteAsync_QuotaExceeded_RejectsBeforeLlm(bool daily, int retryAfter)
    {
        // STEP-01: Create fixture as AgentUseCaseFixture.
        var fixture = new AgentUseCaseFixture();
        // STEP-02: Stub fixture.CountAttemptsAsync.
        fixture.Repository.Setup(x => x.CountAttemptsAsync(3, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, DateTime since, CancellationToken _) => daily ? (since == AgentUseCaseFixture.Now.AddDays(-1) ? 100 : 0) : 60);
        // STEP-03: Wrap Create().ExecuteAsync in act so its outcome can be asserted.
        var act = () => Create(fixture).ExecuteAsync(7, Request(), CancellationToken.None);
        // STEP-04: Assert act throws AgentRateLimitedException and the thrown exception.RetryAfterSeconds is retryAfter.
        (await act.Should().ThrowAsync<AgentRateLimitedException>()).Which.RetryAfterSeconds.Should().Be(retryAfter);
        // STEP-05: Verify fixture receives no other calls.
        fixture.Llm.VerifyNoOtherCalls();
    }

    // TEST-08: Provider failure resets rolled-back tracking before releasing only its own lease.
    /// <summary>Prevents a recovery flush from persisting a partially prepared proposal.</summary>
    [Fact]
    public async Task ExecuteAsync_ProviderFailure_ReleasesLeaseWithCleanTracking()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture.
        var fixture = new AgentUseCaseFixture();
        // STEP-02: Stub fixture.RunAsync.
        fixture.Llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync((AgentLlmStep?)null);
        // STEP-03: Assert Create().ExecuteAsync() throws ServiceUnavailableException.
        await ((Func<Task>)(async () => await Create(fixture).ExecuteAsync(7, Request(), CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();
        // STEP-04: Verify ResetTracking is called once.
        fixture.Repository.Verify(x => x.ResetTracking(), Times.Once);
        // STEP-05: Assert fixture.Conversation.ProcessingAttemptId is null and fixture.Message.ProcessingStatus is Enums.AgentProcessingStatus.Failed.
        fixture.Conversation.ProcessingAttemptId.Should().BeNull(); fixture.Message!.ProcessingStatus.Should().Be(ANGI.Domain.Enums.AgentProcessingStatus.Failed);
    }

    // TEST-09: Equivalent decimal scales and province casing do not turn a completed business retry into a different message.
    /// <summary>Checks canonical hashing ignores representation differences while retaining business identity.</summary>
    [Fact]
    public async Task ExecuteAsync_EquivalentStateRepresentation_Replays()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, get request from Request and set request.ConfirmedState to a new AgentState.
        var fixture = new AgentUseCaseFixture(); var request = Request();
        request.ConfirmedState = new AgentState { Province = "Da Nang", NumDays = 2, BudgetAmount = 500000.0m };
        // STEP-02: Build useCase under test through Create.
        // STEP-03: Run useCase.ExecuteAsync with request.
        var useCase = Create(fixture); await useCase.ExecuteAsync(7, request, CancellationToken.None);
        // STEP-04: Set request.ConfirmedState to a copy of request.ConfirmedState.
        request.ConfirmedState = request.ConfirmedState with { Province = "DA NANG", BudgetAmount = 500000m };
        // STEP-05: Assert useCase.ExecuteAsync().Replayed is true.
        (await useCase.ExecuteAsync(7, request, CancellationToken.None)).Replayed.Should().BeTrue();
    }

    // TEST-10: Explicit save without a preview produces a natural answer without calling the provider or creating a roadmap.
    /// <summary>Supports chat-only saving without asking the client to construct a confirm request.</summary>
    [Fact]
    public async Task ExecuteAsync_SaveWithoutPreview_AnswersWithoutLlm()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, get request from Request and set request.Content to a test text.
        var fixture = new AgentUseCaseFixture(); var request = Request(); request.Content = "lưu lịch trình này";
        // STEP-02: Run Create().ExecuteAsync with request and keep result.
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);
        // STEP-03: Assert result.Commit is null and result.Reply is AgentReplyBuilder.NothingToSave.
        result.Commit.Should().BeNull(); result.Reply.Should().Be(AgentReplyBuilder.NothingToSave);
        // STEP-04: Verify fixture receives no other calls.
        fixture.Llm.VerifyNoOtherCalls(); fixture.Roadmaps.VerifyNoOtherCalls();
    }

    // TEST-11: A short affirmative accepts only the pending constraint snapshot and never saves a roadmap.
    /// <summary>Checks both acceptance and rejection without trusting model-generated consent.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_PendingChange_RequiresNextTurnConsent(bool accept)
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, get state from AgentTestData.State, set fixture.Conversation.State to AgentJson.Write() and 2 more.
        var fixture = new AgentUseCaseFixture();
        var state = AgentTestData.State();
        fixture.Conversation.State = AgentJson.Write(state with { PendingQuestion = "Tăng ngân sách?", PendingStateChange = state with { BudgetAmount = 700000 } });
        var request = Request(); request.Content = "câu trả lời nào cũng được";
        // STEP-02: Stub the model's reading, then its answer on the replan, and record the budget and replan flag of each call, because the context object is reused.
        var calls = new List<(bool Replan, decimal? Budget, string? Awaited)>();
        var answers = new Queue<AgentLlmStep>([Answered(accept ? "accept" : "decline"), new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = "Đã xem lại nhé." })]);
        fixture.Llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .Callback<AgentContext, bool, CancellationToken>((context, _, _) => calls.Add((context.ReplanAfterConfirmation, context.State.BudgetAmount, context.AwaitedAnswer)))
            .ReturnsAsync(() => answers.Dequeue());
        // STEP-03: Run Create().ExecuteAsync with request and keep result.
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);
        // STEP-04: Read saved from the response; assert saved.BudgetAmount is accept ? 700000 : 500000, saved.PendingStateChange is null and 1 more.
        var saved = AgentJson.Read<AgentState>(fixture.Conversation.State);
        saved.BudgetAmount.Should().Be(accept ? 700000 : 500000); saved.PendingStateChange.Should().BeNull(); saved.PendingQuestion.Should().BeNull();
        // STEP-05: Assert result.Commit is null.
        result.Commit.Should().BeNull();
        // STEP-06: Assert the model read the reply against the open question, and only an accepted change planned again with the new budget.
        calls[0].Should().Be((false, 500000m, AgentConfirmationPolicy.AwaitingStateChange));
        if (accept) calls.Should().HaveCount(2).And.Contain((true, 700000m, null));
        else calls.Should().HaveCount(1);
        fixture.Roadmaps.VerifyNoOtherCalls();
    }

    // TEST-11B: Whatever the model reads, a save question only commits on accept for its exact proposal version; covers a refusal, an unclear reply, and an accept with no matching question.
    /// <summary>"Đừng nhé" once saved the plan being refused, because a word list read it as "đúng"; no reading here may write the roadmap.</summary>
    [Theory]
    [InlineData("decline", 1, "Mình chưa lưu")]
    [InlineData("unclear", 1, null)]
    [InlineData("accept", 2, null)]
    public async Task ExecuteAsync_SaveQuestion_OnlyAcceptForItsVersionCommits(string reading, int askedVersion, string? reply)
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, get snapshot from AgentTestData.Snapshot, set the question about proposal 9 and get request from Request.
        var fixture = new AgentUseCaseFixture();
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        var dated = snapshot with { State = snapshot.State with { StartDate = new DateOnly(2026, 12, 1) } };
        fixture.Conversation.CurrentProposalId = 9;
        fixture.Conversation.State = AgentJson.Write(dated.State with { PendingSave = new(9, askedVersion, AgentConfirmationPolicy.StageConfirm) });
        // STEP-02: Read proposal through ReadyProposal and stub the repository and the model's reading.
        var proposal = ReadyProposal(9, dated);
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Llm.Setup(llm => llm.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = "Bạn muốn làm gì tiếp theo?", PendingAnswer = reading }));
        var request = Request(); request.Content = "đừng nhé";

        // STEP-03: Run Create().ExecuteAsync with request and keep result.
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);

        // STEP-04: Assert nothing was committed, the proposal stays ready, and a refusal says so.
        result.Commit.Should().BeNull();
        proposal.Status.Should().Be(ANGI.Domain.Enums.AgentProposalStatus.Ready);
        if (reply is not null) result.Reply.Should().Contain(reply);
        fixture.Roadmaps.Verify(x => x.LockAndGetTrackedAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    // TEST-12: Ordinary or negated chat is sent to the model and cannot reach the deterministic save writer.
    /// <summary>Protects against ambiguous agreement and negative save instructions.</summary>
    [Theory]
    [InlineData("đồng ý")]
    [InlineData("đừng lưu lịch trình này")]
    public async Task ExecuteAsync_NotExplicitSave_NeverCommits(string content)
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, get request from Request and set request.Content to content.
        var fixture = new AgentUseCaseFixture(); var request = Request(); request.Content = content;
        // STEP-02: Assert Create().ExecuteAsync().Commit is null.
        (await Create(fixture).ExecuteAsync(7, request, CancellationToken.None)).Commit.Should().BeNull();
        // STEP-03: Verify RunAsync is called once and fixture receives no other calls.
        fixture.Llm.Verify(llm => llm.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Roadmaps.VerifyNoOtherCalls();
    }

    // TEST-13: Repeated prompt-only text creates distinct messages instead of replaying or conflicting with an earlier turn.
    /// <summary>Checks automatic allocation, a changed revision and retrying the returned ID without provider usage.</summary>
    [Fact]
    public async Task ExecuteAsync_PromptOnly_AllocatesFreshIdentityAndCanReplay()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture.
        // STEP-02: Build useCase under test through Create.
        var fixture = new AgentUseCaseFixture(); var useCase = Create(fixture);
        // STEP-03: Create request as SendAgentMessageRequestDto.
        var request = new SendAgentMessageRequestDto { Content = "bỏ bản nháp" };
        // STEP-04: Run useCase.ExecuteAsync with request and keep first.
        var first = await useCase.ExecuteAsync(7, request, CancellationToken.None);
        // STEP-05: Run useCase.ExecuteAsync with request and keep second.
        var second = await useCase.ExecuteAsync(7, request, CancellationToken.None);
        // STEP-06: Assert second.ClientMessageId is greater than first.ClientMessageId.Value, second.Replayed is false and 3 more.
        second.ClientMessageId.Should().BeGreaterThan(first.ClientMessageId!.Value);
        second.Replayed.Should().BeFalse(); second.ConversationVersion.Should().BeGreaterThan(first.ConversationVersion);
        request.ClientMessageId.Should().BeNull(); request.ExpectedConversationVersion.Should().BeNull();
        // STEP-07: Set request.ClientMessageId to second.ClientMessageId.
        request.ClientMessageId = second.ClientMessageId;
        // STEP-08: Run useCase.ExecuteAsync with request and keep replay.
        var replay = await useCase.ExecuteAsync(7, request, CancellationToken.None);
        // STEP-09: Assert replay.Replayed is true and replay.ClientMessageId is second.ClientMessageId.
        replay.Replayed.Should().BeTrue(); replay.ClientMessageId.Should().Be(second.ClientMessageId);
        // STEP-10: Verify fixture receives no other calls.
        fixture.Llm.VerifyNoOtherCalls();
    }

    // TEST-14: Supplying a stale version remains an explicit conflict even without a client-selected message ID.
    /// <summary>Checks that prompt-only defaults do not silently ignore requested concurrency protection.</summary>
    [Fact]
    public async Task ExecuteAsync_OptionalIdWithExplicitStaleVersion_StillConflicts()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture and set fixture.Conversation.Version to 3.
        var fixture = new AgentUseCaseFixture(); fixture.Conversation.Version = 3;
        // STEP-02: Wrap Create().ExecuteAsync in act so its outcome can be asserted.
        var act = () => Create(fixture).ExecuteAsync(7, new() { Content = "bỏ bản nháp", ExpectedConversationVersion = 1 }, CancellationToken.None);
        // STEP-03: Assert act throws ConflictException and the thrown exception.ErrorCode is "CONVERSATION_VERSION_CONFLICT".
        (await act.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("CONVERSATION_VERSION_CONFLICT");
        // STEP-04: Verify fixture receives no other calls.
        fixture.Llm.VerifyNoOtherCalls();
    }

    // TEST-15: Prompt-only admission retains the busy, expiry and quota guards before provider execution.
    /// <summary>Checks that omission of transport metadata cannot bypass existing admission limits.</summary>
    [Theory]
    [InlineData("busy", "CONVERSATION_BUSY")]
    [InlineData("expired", "CONVERSATION_EXPIRED")]
    [InlineData("quota", "AGENT_RATE_LIMITED")]
    public async Task ExecuteAsync_PromptOnly_StillEnforcesAdmissionGuards(string guard, string code)
    {
        // STEP-01: Create fixture as AgentUseCaseFixture and 3 more.
        var fixture = new AgentUseCaseFixture();
        if (guard == "busy") { fixture.Conversation.ProcessingTurnId = 1; fixture.Conversation.ProcessingAttemptId = 2; fixture.Conversation.ProcessingUntil = AgentUseCaseFixture.Now.AddSeconds(30); }
        if (guard == "expired") fixture.Conversation.ExpiresAt = AgentUseCaseFixture.Now;
        if (guard == "quota") fixture.Repository.Setup(x => x.CountAttemptsAsync(3, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(100);
        // STEP-02: Wrap Create().ExecuteAsync in act so its outcome can be asserted.
        var act = () => Create(fixture).ExecuteAsync(7, new() { Content = "Tôi muốn ăn ở Việt Nam" }, CancellationToken.None);
        // STEP-03: Assert act throws AppException and the thrown exception.ErrorCode is code.
        (await act.Should().ThrowAsync<AppException>()).Which.ErrorCode.Should().Be(code);
        // STEP-04: Verify fixture receives no other calls.
        fixture.Llm.VerifyNoOtherCalls();
    }

    // TEST-16: A wish to save the model read over a ready preview asks first and never saves or replans, whatever the wording.
    /// <summary>The model reads how people say it; the backend then names this exact proposal in a question before anything is written.</summary>
    [Fact]
    public async Task ExecuteAsync_SaveWishTheModelRead_AsksWithoutCommit()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture and set fixture.Conversation.CurrentProposalId to 9.
        var fixture = new AgentUseCaseFixture(); fixture.Conversation.CurrentProposalId = 9;
        // STEP-02: Read proposal through ReadyProposal.
        var proposal = ReadyProposal(9, AgentTestData.Snapshot(AgentTestData.Item(), AgentTestData.Item(1, "b", 2)));
        // STEP-03: Stub fixture.GetProposalSnapshotAsync and fixture.GetProposalAsync.
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Llm.Setup(llm => llm.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "request_save", Reply = "" }));
        // STEP-04: Get request from Request, set the proposal's start date and set request.Content to a test text.
        var request = Request(); request.Content = "ưng rồi đó";
        var dated = AgentTestData.Snapshot(AgentTestData.Item(), AgentTestData.Item(1, "b", 2));
        proposal.Payload = AgentJson.Write(dated with { State = dated.State with { StartDate = new DateOnly(2026, 12, 1) } });

        // STEP-05: Run Create().ExecuteAsync with request and keep result.
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);

        // STEP-06: Assert result.Reply is the expected text, result.Commit is null and the question names this proposal version.
        result.Reply.Should().Be("Mình lưu lịch hiện tại (2 món, tổng 0 đồng/người) nhé?");
        result.Commit.Should().BeNull();
        AgentJson.Read<AgentState>(fixture.Conversation.State).PendingSave.Should().Be(new AgentPendingSave(9, 1, AgentConfirmationPolicy.StageConfirm));
        // STEP-07: Verify the roadmaps were not touched.
        fixture.Roadmaps.VerifyNoOtherCalls();
    }

    // TEST-16B: A wish to save while a clarification is open drops that question and asks about saving, writing nothing yet.
    /// <summary>In conversation 193 "chốt đi" after an open question rebuilt the whole plan because a save wish was not allowed there.</summary>
    [Fact]
    public async Task ExecuteAsync_SaveWishWithOpenQuestion_AsksAboutSaving()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, get a dated snapshot and open a clarification over its proposal.
        var fixture = new AgentUseCaseFixture();
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        var dated = snapshot with { State = snapshot.State with { StartDate = new DateOnly(2026, 12, 1) } };
        fixture.Conversation.CurrentProposalId = 9;
        fixture.Conversation.State = AgentJson.Write(dated.State with { PendingQuestion = "Bạn muốn đổi bữa nào?" });
        // STEP-02: Read proposal through ReadyProposal and stub the repository and the model's reading.
        var proposal = ReadyProposal(9, dated);
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Llm.Setup(llm => llm.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "request_save", Reply = "" }));
        var request = Request(); request.Content = "chốt đi";

        // STEP-03: Run Create().ExecuteAsync with request and keep result.
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);

        // STEP-04: Assert the save question names this proposal, the clarification is gone and nothing was committed.
        result.Reply.Should().StartWith("Mình lưu lịch hiện tại");
        result.Commit.Should().BeNull();
        var saved = AgentJson.Read<AgentState>(fixture.Conversation.State);
        saved.PendingQuestion.Should().BeNull();
        saved.PendingSave.Should().Be(new AgentPendingSave(9, 1, AgentConfirmationPolicy.StageConfirm));
    }

    /// <summary>Creates a complete real use case over isolated persistence/provider boundaries.</summary>
    // TEST-17: A refusal the model read in an open clarification over a ready preview closes it and the plan stays current.
    /// <summary>Gives the user a way past a question so "chốt lịch này" works next, however they say no.</summary>
    [Theory]
    [InlineData("giữ nguyên")][InlineData("thôi dẹp đi")]
    public async Task ExecuteAsync_DeclineQuestionOverReadyProposal_ClearsIt(string content)
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, get snapshot from AgentTestData.Snapshot, set fixture.Conversation.CurrentProposalId to 9 and 1 more.
        var fixture = new AgentUseCaseFixture();
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        fixture.Conversation.CurrentProposalId = 9;
        fixture.Conversation.State = AgentJson.Write(snapshot.State with { PendingQuestion = "Bạn muốn đổi bữa nào?" });
        // STEP-02: Read proposal through ReadyProposal.
        var proposal = ReadyProposal(9, snapshot);
        // STEP-03: Stub fixture.GetProposalSnapshotAsync and fixture.GetProposalAsync.
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Llm.Setup(llm => llm.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(Answered("decline"));
        // STEP-04: Get request from Request and set request.Content to content.
        var request = Request(); request.Content = content;

        // STEP-05: Run Create().ExecuteAsync with request and keep result.
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);

        // STEP-06: Assert result.Reply contains the expected text, result.Proposal is not null and AgentJson.Read().PendingQuestion is null.
        result.Reply.Should().Contain("Mình giữ nguyên lịch hiện tại");
        result.Proposal.Should().NotBeNull();
        AgentJson.Read<AgentState>(fixture.Conversation.State).PendingQuestion.Should().BeNull();
    }

    // TEST-18: Declining after an accepted change that produced no preview returns to the reviewed constraints, so the preview can be saved.
    /// <summary>Prevents "giữ nguyên" from leaving a state that no longer matches the preview and blocks saving.</summary>
    [Fact]
    public async Task ExecuteAsync_DeclineAfterFailedChange_RestoresReviewedConstraints()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, get snapshot from AgentTestData.Snapshot, set fixture.Conversation.CurrentProposalId to 9 and 1 more.
        var fixture = new AgentUseCaseFixture();
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        fixture.Conversation.CurrentProposalId = 9;
        fixture.Conversation.State = AgentJson.Write(snapshot.State with { PendingQuestion = "Chưa có món?", MealRequests = [new(1, "lunch", null, "Quán khác", [])] });
        // STEP-02: Read proposal through ReadyProposal.
        var proposal = ReadyProposal(9, snapshot);
        // STEP-03: Stub fixture.GetProposalSnapshotAsync and fixture.GetProposalAsync.
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Llm.Setup(llm => llm.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(Answered("decline"));
        // STEP-04: Get request from Request and set request.Content to a test text.
        var request = Request(); request.Content = "giữ nguyên";

        // STEP-05: Run Create().ExecuteAsync with request.
        await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);

        // STEP-06: Assert AgentJson.Read().MealRequests is empty.
        AgentJson.Read<AgentState>(fixture.Conversation.State).MealRequests.Should().BeEmpty();
    }

    // TEST-19: A save with an open clarification names that question and points to the way out.
    /// <summary>Prevents a dead end where the user is told to answer a question that is not shown.</summary>
    [Fact]
    public async Task ExecuteAsync_SaveWithOpenQuestion_NamesTheQuestion()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, get snapshot from AgentTestData.Snapshot, set fixture.Conversation.CurrentProposalId to 9 and 1 more.
        var fixture = new AgentUseCaseFixture();
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        fixture.Conversation.CurrentProposalId = 9;
        fixture.Conversation.State = AgentJson.Write(snapshot.State with { PendingQuestion = "Bạn muốn đổi bữa nào?" });
        // STEP-02: Read proposal through ReadyProposal.
        var proposal = ReadyProposal(9, snapshot);
        // STEP-03: Stub fixture.GetProposalSnapshotAsync and fixture.GetProposalAsync.
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        // STEP-04: Get request from Request and set request.Content to a test text.
        var request = Request(); request.Content = "chốt lịch này";

        // STEP-05: Run Create().ExecuteAsync with request and keep result.
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);

        // STEP-06: Assert result.Commit is null and result.Reply contains the expected text.
        result.Commit.Should().BeNull();
        result.Reply.Should().Contain("Bạn muốn đổi bữa nào?").And.Contain("giữ nguyên");
    }

    // TEST-20: A reset on a conversation whose roadmap was deleted starts empty and detaches it instead of failing with 404.
    /// <summary>Gives a conversation attached to a deleted roadmap a way forward; every other message would be 404.</summary>
    [Fact]
    public async Task ExecuteAsync_ResetAfterRoadmapDeleted_DetachesAndStartsEmpty()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, set fixture.Conversation.RoadmapId to 38, set fixture.Conversation.State to AgentJson.Write() and 2 more.
        var fixture = new AgentUseCaseFixture();
        fixture.Conversation.RoadmapId = 38;
        fixture.Conversation.State = AgentJson.Write(AgentTestData.State());
        var request = Request(); request.Content = "làm lại từ đầu";

        // STEP-02: Run Create().ExecuteAsync with request and keep result.
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);

        // STEP-03: Assert result.Reply starts with the expected text, fixture.Conversation.RoadmapId is null and AgentJson.Read().Province is null.
        result.Reply.Should().StartWith("Đã bắt đầu kế hoạch mới.");
        fixture.Conversation.RoadmapId.Should().BeNull();
        AgentJson.Read<AgentState>(fixture.Conversation.State).Province.Should().BeNull();
        // STEP-04: Verify fixture receives no other calls.
        fixture.Llm.VerifyNoOtherCalls();
    }

    // TEST-21: A greeting with an open question keeps the current proposal as the result and the question open, with no "previous proposal kept" note.
    /// <summary>Prevents the fixed note from being appended to a social reply and verifies the turn is still stored and replayable.</summary>
    [Fact]
    public async Task ExecuteAsync_GreetingWithOpenQuestion_KeepsProposalWithoutExtraNote()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, set fixture.Conversation.CurrentProposalId to 9, get snapshot from AgentTestData.Snapshot and 1 more.
        var fixture = new AgentUseCaseFixture(); fixture.Conversation.CurrentProposalId = 9;
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        fixture.Conversation.State = AgentJson.Write(snapshot.State with { PendingQuestion = "Bạn muốn đổi món nào?" });
        // STEP-02: Read proposal through ReadyProposal.
        var proposal = ReadyProposal(9, snapshot);
        // STEP-03: Stub fixture.GetProposalSnapshotAsync and fixture.GetProposalAsync.
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        // STEP-04: Stub fixture.RunAsync with the model's greeting, get request from Request and set request.Content to a test text.
        fixture.Llm.Setup(llm => llm.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "chat", Reply = "Chào bạn nha!" }));
        var request = Request(); request.Content = "Xin chào";
        // STEP-05: Build useCase under test through Create.
        var useCase = Create(fixture);

        // STEP-06: Run useCase.ExecuteAsync with request and keep result.
        var result = await useCase.ExecuteAsync(7, request, CancellationToken.None);
        // STEP-07: Run useCase.ExecuteAsync with request and keep replay.
        var replay = await useCase.ExecuteAsync(7, request, CancellationToken.None);

        // STEP-08: Assert result.Reply is the expected text and AgentJson.Read().PendingQuestion is the expected text.
        result.Reply.Should().Be("Chào bạn nha!\nBạn muốn đổi món nào?");
        AgentJson.Read<AgentState>(fixture.Conversation.State).PendingQuestion.Should().Be("Bạn muốn đổi món nào?");
        // STEP-09: Assert proposal.Status is Enums.AgentProposalStatus.Ready and fixture.Conversation.CurrentProposalId is 9.
        proposal.Status.Should().Be(ANGI.Domain.Enums.AgentProposalStatus.Ready);
        fixture.Conversation.CurrentProposalId.Should().Be(9);
        // STEP-10: Assert replay.Reply is result.Reply and replay.Replayed is true.
        replay.Reply.Should().Be(result.Reply); replay.Replayed.Should().BeTrue();
        // STEP-11: Verify the model is called once, the replay costing nothing.
        fixture.Llm.Verify(llm => llm.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-22: An out-of-scope message over a ready proposal keeps it committable: no new proposal, and the same proposal returned as still current.
    /// <summary>A refusal must not change the plan or block a later save.</summary>
    [Fact]
    public async Task ExecuteAsync_OutOfScope_KeepsReadyProposalCurrent()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, set fixture.Conversation.CurrentProposalId to 9, get snapshot from AgentTestData.Snapshot and 1 more.
        var fixture = new AgentUseCaseFixture(); fixture.Conversation.CurrentProposalId = 9;
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        fixture.Conversation.State = AgentJson.Write(snapshot.State);
        // STEP-02: Read proposal through ReadyProposal.
        var proposal = ReadyProposal(9, snapshot);
        // STEP-03: Stub fixture.GetProposalSnapshotAsync, fixture.GetProposalAsync and fixture.RunAsync.
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Llm.Setup(llm => llm.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "out_of_scope", Reply = "Draft" }));
        // STEP-04: Get request from Request and set request.Content to a test text.
        var request = Request(); request.Content = "Hướng dẫn bóc cam";

        // STEP-05: Run Create().ExecuteAsync with request and keep result.
        var result = await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);

        // STEP-06: Assert result.Reply is one of the allowed values, result.Proposal.ProposalId is 9 and proposal.Status is Enums.AgentProposalStatus.Ready.
        result.Reply.Should().BeOneOf(AgentReplyGuard.OutOfScopeReplies);
        result.Proposal!.ProposalId.Should().Be(9);
        proposal.Status.Should().Be(ANGI.Domain.Enums.AgentProposalStatus.Ready);
        // STEP-07: Verify AddProposal is never called and RunAsync is called once.
        fixture.Repository.Verify(repo => repo.AddProposal(It.IsAny<ANGI.Domain.Entities.AgentProposal>()), Times.Never);
        fixture.Llm.Verify(llm => llm.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-23: A dish suggestion ends an open save question so the next "ok" cannot save the plan; a greeting keeps it. Covers both.
    /// <summary>"Lưu nhé?", then "gợi ý 1 món", then "ok" would otherwise commit the plan while the user was answering the offer.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_SuggestionDuringSaveQuestion_EndsTheQuestion(bool suggestion)
    {
        // STEP-01: Create fixture as AgentUseCaseFixture with a ready proposal and a state holding an open save question.
        var fixture = new AgentUseCaseFixture(); fixture.Conversation.CurrentProposalId = 9;
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        fixture.Conversation.State = AgentJson.Write(snapshot.State with { PendingSave = new(9, 1, "confirm") });
        var proposal = ReadyProposal(9, snapshot);
        fixture.Repository.Setup(repo => repo.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        fixture.Repository.Setup(repo => repo.GetProposalAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        // STEP-02: Stub fixture.RunAsync with a suggestion and its wording, or a greeting.
        var call = new AgentToolCall("s", "suggest_dish", System.Text.Json.JsonSerializer.SerializeToElement(new AgentSuggestionInput(TagIds: [], ExcludeTagIds: [], TasteTargets: []), AgentJson.Options));
        var greeting = new AgentLlmStep([], new AgentOutput { Action = "chat", Reply = "Mình nghe đây nè!" });
        if (suggestion) fixture.Llm.SetupSequence(llm => llm.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([call], null)).ReturnsAsync(greeting);
        else fixture.Llm.Setup(llm => llm.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(greeting);
        // STEP-03: Get request from Request and set request.Content.
        var request = Request(); request.Content = suggestion ? "gợi ý cho tôi 1 món bất kì" : "bạn khỏe không";

        // STEP-04: Run Create().ExecuteAsync with request.
        await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);

        // STEP-05: Assert the stored state drops the save question and marks the offer after a suggestion, and keeps the question after a greeting.
        var state = AgentJson.Read<AgentState>(fixture.Conversation.State);
        state.PendingSave.Should().Be(suggestion ? null : new AgentPendingSave(9, 1, "confirm"));
        state.DishOffered.Should().Be(suggestion ? true : null);
    }

    // TEST-24: The mark of an offered dish lives one turn: the model is told, and the stored state no longer holds it.
    /// <summary>A later "ok" must not be read against a suggestion several messages old.</summary>
    [Fact]
    public async Task ExecuteAsync_OfferedDish_LivesOneTurn()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture with a stored offer and keep what the model is told.
        var fixture = new AgentUseCaseFixture();
        fixture.Conversation.State = AgentJson.Write(AgentTestData.State() with { DishOffered = true });
        var told = new List<(bool Offered, bool? Stored)>();
        fixture.Llm.Setup(llm => llm.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<AgentContext, bool, CancellationToken>((context, _, _) => told.Add((context.DishOffered, context.State.DishOffered)))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "chat", Reply = "Mình nghe đây nè!" }));
        // STEP-02: Get request from Request and set request.Content.
        var request = Request(); request.Content = "bạn khỏe không";

        // STEP-03: Run Create().ExecuteAsync with request.
        await Create(fixture).ExecuteAsync(7, request, CancellationToken.None);

        // STEP-04: Assert the model was told through the context only and the stored state no longer holds the mark.
        told.Should().Equal((true, (bool?)null));
        AgentJson.Read<AgentState>(fixture.Conversation.State).DishOffered.Should().BeNull();
    }

    private static SendAgentMessageUseCase Create(AgentUseCaseFixture fixture) => new(new SendAgentMessageRequestDtoValidator(fixture.Limits),
        fixture.Repository.Object, fixture.Roadmaps.Object, fixture.User.Object, fixture.UnitOfWork.Object, fixture.Clock, fixture.Limits,
        new AgentContextBuilder(fixture.Repository.Object, fixture.Roadmaps.Object, fixture.Limits, fixture.Clock, AgentTestData.Validator().Tags.Object), fixture.Orchestrator(), fixture.Telemetry, fixture.Committer());
    /// <summary>Creates the minimal valid logical user request.</summary>
    private static SendAgentMessageRequestDto Request() => new() { ClientMessageId = 1, ExpectedConversationVersion = 1, Content = "Explain the proposal" };
}
