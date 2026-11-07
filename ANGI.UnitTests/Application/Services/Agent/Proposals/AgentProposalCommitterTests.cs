using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Proposals;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.UnitTests.Application.UseCases.Agent;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent.Proposals;

/// <summary>Verifies AgentProposalCommitter queues impression events only for dishes that reco actually ranked.</summary>
public sealed class AgentProposalCommitterTests
{
    // TEST-01: Committed items are grouped into one impression event per recommendation request with distinct dishes, and fallback items without a request id are skipped.
    /// <summary>Ensures reco receives one deduplicated impression per request and never an impression for a fallback dish it did not rank.</summary>
    [Fact]
    public void EnqueueImpressions_GroupsDistinctDishesByRequestAndSkipsFallback()
    {
        // STEP-01: Create request through Guid.NewGuid.
        var request = Guid.NewGuid();
        // STEP-02: Create events as List<OutboxMessage>; mock IAgentRepository; stub repository.AddOutboxMessage.
        var repository = new Mock<IAgentRepository>();
        var events = new List<OutboxMessage>();
        repository.Setup(x => x.AddOutboxMessage(It.IsAny<OutboxMessage>())).Callback<OutboxMessage>(events.Add);
        // STEP-03: Get snapshot from AgentTestData.Snapshot and call AgentProposalCommitter.EnqueueImpressions.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item() with { RequestId = request },
            AgentTestData.Item() with { ItemKey = "second", RequestId = request },
            AgentTestData.Item() with { ItemKey = "fallback", DishId = 2, RequestId = null });
        AgentProposalCommitter.EnqueueImpressions(repository.Object, snapshot, 9, 3, DateTime.UtcNow);
        // STEP-04: Read payload from the response; assert events has exactly one matching item, message.EventType is AgentImpressionRecorded and 2 more.
        var message = events.Should().ContainSingle().Subject;
        message.EventType.Should().Be(OutboxEventType.AgentImpressionRecorded);
        using var payload = JsonDocument.Parse(message.Payload);
        payload.RootElement.GetProperty("agent_proposal_id").GetInt64().Should().Be(9);
        payload.RootElement.GetProperty("dish_ids").GetArrayLength().Should().Be(1);
    }

    // TEST-02: A stored open question blocks PrepareAsync with PROPOSAL_NOT_COMMITTABLE, while the state a chat save turn ends in, with that question closed, lets the same proposal through; covers both.
    /// <summary>Ensures a chat save that closes the open question is judged on its own turn state, and the confirm endpoint, which gives none, still reads the stored state.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_StoredOpenQuestion_JudgesTurnStateWhenGiven(bool withTurnState)
    {
        // STEP-01: Create fixture with ready current proposal 9 and a stored state that still holds an open question.
        var fixture = new AgentUseCaseFixture(); fixture.Conversation.CurrentProposalId = 9;
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        fixture.Conversation.State = AgentJson.Write(snapshot.State with { PendingQuestion = "Bạn muốn đổi món nào?" });
        var proposal = new AgentProposal { Id = 9, ConversationId = 7, ProposalVersion = 2, Status = AgentProposalStatus.Ready,
            ExpiresAt = AgentUseCaseFixture.Now.AddDays(1), Payload = AgentJson.Write(snapshot) };
        // STEP-02: Stub fixture.Repository.GetProposalSnapshotAsync to return proposal.
        fixture.Repository.Setup(x => x.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(proposal);

        // STEP-03: Wrap PrepareAsync, with the closed-question turn state or none, in act so its outcome can be asserted.
        var act = () => fixture.Committer().PrepareAsync(fixture.Conversation, 3, new() { ProposalId = 9, ProposalVersion = 2 }, true,
            CancellationToken.None, withTurnState ? snapshot.State : null);

        // STEP-04: Assert the turn state prepares proposal 9, and no turn state throws PROPOSAL_NOT_COMMITTABLE.
        if (withTurnState) (await act()).ProposalId.Should().Be(9);
        else (await act.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("PROPOSAL_NOT_COMMITTABLE");
    }
}
