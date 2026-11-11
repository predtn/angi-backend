using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using ANGI.Application.UseCases.Agent.GetConversation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
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

    // TEST-03: A long conversation comes back whole up to the transcript limit, not the model's shorter window, in order and from the reset point.
    /// <summary>Conversation 158 showed 12 of its 50 messages after a reload because the screen shared the model's window.</summary>
    [Fact]
    public async Task ExecuteAsync_LongConversation_UsesTranscriptLimit()
    {
        // STEP-01: Create fixture as AgentUseCaseFixture and stub GetCompletedHistoryAsync for the transcript limit with 50 messages.
        var fixture = new AgentUseCaseFixture();
        var messages = Enumerable.Range(1, 50).Select(sequence => new AgentMessage { ConversationId = 7, SequenceNo = sequence, TurnId = sequence,
            Role = sequence % 2 == 1 ? AgentMessageRole.User : AgentMessageRole.Assistant, Payload = AgentJson.Write(new AgentStoredMessage($"Tin {sequence}")) }).ToArray();
        fixture.Repository.Setup(x => x.GetCompletedHistoryAsync(7, fixture.Limits.MaxTranscriptMessages, It.IsAny<CancellationToken>())).ReturnsAsync(messages);
        var useCase = new GetAgentConversationUseCase(fixture.Repository.Object, fixture.User.Object, fixture.Clock, fixture.Limits);

        // STEP-02: Run useCase.ExecuteAsync and keep result.
        var result = await useCase.ExecuteAsync(7, CancellationToken.None);

        // STEP-03: Assert every message is returned in order.
        result.Messages.Should().HaveCount(50);
        result.Messages[0].Content.Should().Be("Tin 1");
        result.Messages[^1].Content.Should().Be("Tin 50");
        // STEP-04: Verify the model's window was not used for the screen.
        fixture.Repository.Verify(x => x.GetCompletedHistoryAsync(7, fixture.Limits.MaxHistoryMessages, It.IsAny<CancellationToken>()), Times.Never);
    }
}
