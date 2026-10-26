using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent;
using ANGI.Application.UseCases.Agent.SendMessage;
using ANGI.Application.UseCases.Validators.Agent;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Agent.SendMessage;

/// <summary>Verifies idempotent turn admission and isolated failure recovery.</summary>
public sealed class SendAgentMessageUseCaseTests
{
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

    /// <summary>Creates a complete real use case over isolated persistence/provider boundaries.</summary>
    private static SendAgentMessageUseCase Create(AgentUseCaseFixture fixture) => new(new SendAgentMessageRequestDtoValidator(fixture.Limits),
        fixture.Repository.Object, fixture.Roadmaps.Object, fixture.User.Object, fixture.UnitOfWork.Object, fixture.Clock, fixture.Limits,
        new AgentContextBuilder(fixture.Repository.Object, fixture.Roadmaps.Object, fixture.Limits, fixture.Clock), fixture.Orchestrator(), fixture.Telemetry);
    /// <summary>Creates the minimal valid logical user request.</summary>
    private static SendAgentMessageRequestDto Request() => new() { ClientMessageId = 1, ExpectedConversationVersion = 1, Content = "Explain the proposal" };
}
