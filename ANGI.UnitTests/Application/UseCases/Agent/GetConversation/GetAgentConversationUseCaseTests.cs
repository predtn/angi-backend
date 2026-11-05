using ANGI.Application.Common.Exceptions;
using ANGI.Application.UseCases.Agent.GetConversation;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Agent.GetConversation;

/// <summary>Verifies ownership-safe restore and request-time expiry without activity renewal.</summary>
public sealed class GetAgentConversationUseCaseTests
{
    // TEST-01: Reading an active conversation restores state and does not write or extend expiry.
    /// <summary>Checks GET's read-only retention contract.</summary>
    [Fact]
    public async Task ExecuteAsync_ActiveState_DoesNotRenewExpiry()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture and derive expires from fixture.Conversation.ExpiresAt.
        var fixture = new AgentUseCaseFixture(); var expires = fixture.Conversation.ExpiresAt;
        // STEP-02: Create the GetAgentConversationUseCase under test as useCase.
        var useCase = new GetAgentConversationUseCase(fixture.Repository.Object, fixture.User.Object, fixture.Clock, fixture.Limits);
        // STEP-03: Run useCase.ExecuteAsync and keep result.
        var result = await useCase.ExecuteAsync(7, CancellationToken.None);
        // STEP-04: Assert result.Version is 1, result.Processing is false and fixture.Conversation.ExpiresAt is expires.
        result.Version.Should().Be(1); result.Processing.Should().BeFalse(); fixture.Conversation.ExpiresAt.Should().Be(expires);
        // STEP-05: Verify fixture receives no other calls.
        fixture.UnitOfWork.VerifyNoOtherCalls();
    }

    // TEST-02: Exact expiry time is expired and another user's missing conversation is concealed.
    /// <summary>Verifies inclusive expiry and ownership error boundaries.</summary>
    [Fact]
    public async Task ExecuteAsync_ExpiryOrMissing_UsesDocumentedErrors()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture and set fixture.Conversation.ExpiresAt to AgentUseCaseFixture.Now.
        var fixture = new AgentUseCaseFixture(); fixture.Conversation.ExpiresAt = AgentUseCaseFixture.Now;
        // STEP-02: Create the GetAgentConversationUseCase under test as useCase.
        var useCase = new GetAgentConversationUseCase(fixture.Repository.Object, fixture.User.Object, fixture.Clock, fixture.Limits);
        // STEP-03: Assert useCase.ExecuteAsync() throws GoneException and useCase.ExecuteAsync() throws NotFoundException.
        await ((Func<Task>)(async () => await useCase.ExecuteAsync(7, CancellationToken.None))).Should().ThrowAsync<GoneException>();
        await ((Func<Task>)(async () => await useCase.ExecuteAsync(99, CancellationToken.None))).Should().ThrowAsync<NotFoundException>();
    }
}
