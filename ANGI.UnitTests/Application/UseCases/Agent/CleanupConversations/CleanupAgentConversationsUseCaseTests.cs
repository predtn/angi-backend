using ANGI.Application.UseCases.Agent.CleanupConversations;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Agent.CleanupConversations;

/// <summary>Verifies cleanup unlinking and retention decisions under one use-case transaction.</summary>
public sealed class CleanupAgentConversationsUseCaseTests
{
    // TEST-01: Expire idle memory, unlink retention-aged proposal pointers and delete only the aged conversation.
    /// <summary>Checks the boundary between expiry and permanent short-term memory removal.</summary>
    [Fact]
    public async Task ExecuteAsync_RetentionBatch_UnlinksThenDeletesOnlyAgedMemory()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture, create recent as AgentConversation and create aged as AgentConversation.
        var fixture = new AgentUseCaseFixture();
        var recent = new AgentConversation { Id = 7, UpdatedAt = AgentUseCaseFixture.Now.AddDays(-8) };
        var aged = new AgentConversation { Id = 8, UpdatedAt = AgentUseCaseFixture.Now.AddDays(-30), CurrentProposalId = 9 };
        // STEP-02: Stub fixture.LockCleanupBatchAsync.
        fixture.Repository.Setup(x => x.LockCleanupBatchAsync(AgentUseCaseFixture.Now, AgentUseCaseFixture.Now.AddDays(-30), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { recent, aged });
        // STEP-03: Run ExecuteAsync and keep result.
        var result = await new CleanupAgentConversationsUseCase(fixture.Repository.Object, fixture.UnitOfWork.Object, fixture.Clock, fixture.Limits).ExecuteAsync(CancellationToken.None);
        // STEP-04: Assert result is 2 and recent.Status is AgentConversationStatus.Expired.
        // STEP-05: Assert aged.CurrentProposalId is null.
        result.Should().Be(2); recent.Status.Should().Be(AgentConversationStatus.Expired); aged.CurrentProposalId.Should().BeNull();
        // STEP-06: Verify RemoveConversationAsync is called once and RemoveConversationAsync is never called.
        fixture.Repository.Verify(x => x.RemoveConversationAsync(aged, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Repository.Verify(x => x.RemoveConversationAsync(recent, It.IsAny<CancellationToken>()), Times.Never);
        // STEP-07: Verify SaveChangesAsync is called 2 times and fixture receives no other calls.
        fixture.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2)); fixture.Roadmaps.VerifyNoOtherCalls();
    }
}
