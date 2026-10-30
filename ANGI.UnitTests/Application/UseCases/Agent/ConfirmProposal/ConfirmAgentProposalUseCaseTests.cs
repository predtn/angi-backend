using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.UseCases.Agent.ConfirmProposal;
using ANGI.Application.UseCases.Validators.Agent;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.UnitTests.Application.Services.Agent;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Agent.ConfirmProposal;

/// <summary>Verifies exact-version and committed replay guards before any fresh business write.</summary>
public sealed class ConfirmAgentProposalUseCaseTests
{
    // TEST-01: Committed replay returns the original revision after conversation expiry without any LLM or transaction.
    /// <summary>Checks the durable idempotency boundary independent of current activity lifetime.</summary>
    [Fact]
    public async Task ExecuteAsync_CommittedReplay_ReturnsOriginalResultAfterExpiry()
    {
        var fixture = new AgentUseCaseFixture(); fixture.Conversation.ExpiresAt = AgentUseCaseFixture.Now;
        var original = new AgentCommitDto(88, DateTime.UnixEpoch.AddSeconds(5), 9, new Dictionary<string, long> { ["a"] = 55 });
        var proposal = new AgentProposal { Id = 9, ConversationId = 7, ProposalVersion = 2, Status = AgentProposalStatus.Committed, CommitResult = AgentJson.Write(original) };
        fixture.Repository.Setup(x => x.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        var result = await Create(fixture).ExecuteAsync(7, new() { ProposalId = 9, ProposalVersion = 2 }, CancellationToken.None);
        result.Should().BeEquivalentTo(original); fixture.UnitOfWork.VerifyNoOtherCalls(); fixture.Llm.VerifyNoOtherCalls();
    }

    // TEST-02: Wrong displayed version, noncurrent status, busy lease and exact proposal expiry reject confirmation.
    /// <summary>Checks first-commit preconditions before route calls or roadmap writes.</summary>
    [Theory]
    [InlineData("version", "PROPOSAL_VERSION_CONFLICT")][InlineData("status", "PROPOSAL_NOT_COMMITTABLE")]
    [InlineData("current", "PROPOSAL_NOT_COMMITTABLE")][InlineData("busy", "CONVERSATION_BUSY")][InlineData("expiry", "PROPOSAL_EXPIRED")]
    public async Task ExecuteAsync_InvalidConfirmation_RejectsBeforeWrites(string area, string code)
    {
        var fixture = new AgentUseCaseFixture(); fixture.Conversation.CurrentProposalId = 9;
        var proposal = new AgentProposal { Id = 9, ConversationId = 7, ProposalVersion = 2, Status = AgentProposalStatus.Ready,
            ExpiresAt = AgentUseCaseFixture.Now.AddDays(1), Payload = AgentJson.Write(AgentTestData.Snapshot(AgentTestData.Item())) };
        if (area == "status") proposal.Status = AgentProposalStatus.Superseded;
        if (area == "current") fixture.Conversation.CurrentProposalId = 8;
        if (area == "busy") { fixture.Conversation.ProcessingAttemptId = 2; fixture.Conversation.ProcessingUntil = AgentUseCaseFixture.Now.AddSeconds(1); }
        if (area == "expiry") proposal.ExpiresAt = AgentUseCaseFixture.Now;
        fixture.Repository.Setup(x => x.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
        var act = () => Create(fixture).ExecuteAsync(7, new ConfirmAgentProposalRequestDto { ProposalId = 9, ProposalVersion = area == "version" ? 1 : 2 }, CancellationToken.None);
        (await act.Should().ThrowAsync<AppException>()).Which.ErrorCode.Should().Be(code); fixture.UnitOfWork.VerifyNoOtherCalls();
    }

    /// <summary>Constructs confirmation over deterministic current-catalog validation.</summary>
    private static ConfirmAgentProposalUseCase Create(AgentUseCaseFixture fixture) => new(new ConfirmAgentProposalRequestDtoValidator(),
        fixture.Repository.Object, fixture.User.Object, fixture.UnitOfWork.Object,
        fixture.Committer(), fixture.Clock, fixture.Limits, fixture.Telemetry);
}
