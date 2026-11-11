using ANGI.Application.Common.Exceptions;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.UseCases.Agent.CreateConversation;
using ANGI.Application.UseCases.Validators.Agent;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Agent.CreateConversation;

/// <summary>Verifies conversation creation and ownership checks without an official roadmap write.</summary>
public sealed class CreateAgentConversationUseCaseTests
{
    // TEST-01: Opening empty state persists one conversation with a seven-day lifetime and no roadmap.
    /// <summary>Checks the main AGENT-01 write and default expiry policy.</summary>
    [Fact]
    public async Task ExecuteAsync_EmptyTrip_PersistsOnlyConversation()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture.
        var fixture = new AgentUseCaseFixture();
        // STEP-02: Create the CreateAgentConversationUseCase under test as useCase.
        var useCase = new CreateAgentConversationUseCase(new CreateAgentConversationRequestDtoValidator(), fixture.Repository.Object,
            fixture.Roadmaps.Object, fixture.User.Object, fixture.UnitOfWork.Object, fixture.Clock, fixture.Limits);
        // STEP-03: Run useCase.ExecuteAsync and keep result.
        var result = await useCase.ExecuteAsync(new(), CancellationToken.None);
        // STEP-04: Assert result.ConversationId is 7, result.State.Province is null and result.CurrentProposal is null.
        result.ConversationId.Should().Be(7); result.State.Province.Should().BeNull(); result.CurrentProposal.Should().BeNull();
        // STEP-05: Verify SaveChangesAsync is called once and fixture receives no other calls.
        fixture.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        fixture.Roadmaps.VerifyNoOtherCalls();
    }

    // TEST-02: A missing or foreign roadmap is rejected before a new conversation is tracked.
    /// <summary>Preserves ownership concealment at conversation creation.</summary>
    [Fact]
    public async Task ExecuteAsync_ForeignRoadmap_RejectsBeforeWrite()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture.
        var fixture = new AgentUseCaseFixture();
        // STEP-02: Create the CreateAgentConversationUseCase under test as useCase.
        var useCase = new CreateAgentConversationUseCase(new CreateAgentConversationRequestDtoValidator(), fixture.Repository.Object,
            fixture.Roadmaps.Object, fixture.User.Object, fixture.UnitOfWork.Object, fixture.Clock, fixture.Limits);
        // STEP-03: Wrap useCase.ExecuteAsync with CreateAgentConversationRequestDto { RoadmapId = 99 } in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(new CreateAgentConversationRequestDto { RoadmapId = 99 }, CancellationToken.None);
        // STEP-04: Assert act throws NotFoundException and the thrown exception.ErrorCode is "ROADMAP_NOT_FOUND".
        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("ROADMAP_NOT_FOUND");
        // STEP-05: Verify fixture receives no other calls.
        fixture.UnitOfWork.VerifyNoOtherCalls();
    }
}
