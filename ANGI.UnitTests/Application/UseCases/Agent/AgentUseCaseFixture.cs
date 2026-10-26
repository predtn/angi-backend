using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using ANGI.Domain.Entities;
using ANGI.UnitTests.Application.Services.Agent;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Agent;

/// <summary>Provides isolated persistence/identity boundaries for mirrored Agent use-case tests.</summary>
internal sealed class AgentUseCaseFixture
{
    public static readonly DateTime Now = new(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
    public Mock<IAgentRepository> Repository { get; } = new();
    public Mock<IRoadmapRepository> Roadmaps { get; } = new();
    public Mock<ICurrentUserService> User { get; } = new();
    public Mock<IUnitOfWork> UnitOfWork { get; } = new();
    public Mock<IUnitOfWorkTransaction> Transaction { get; } = new();
    public Mock<IAgentLlmService> Llm { get; } = new();
    public AgentLimits Limits { get; } = new();
    public IAgentTelemetry Telemetry { get; } = Mock.Of<IAgentTelemetry>();
    public TimeProvider Clock { get; } = new TestClock();
    public AgentConversation Conversation { get; } = new() { Id = 7, UserId = 3, Version = 1,
        State = AgentJson.Write(AgentTestData.State()), ExpiresAt = Now.AddDays(7) };
    public AgentMessage? Message { get; private set; }

    /// <summary>Sets up deterministic leases and observable transaction/persistence boundaries.</summary>
    public AgentUseCaseFixture()
    {
        User.SetupGet(x => x.UserId).Returns(3);
        Repository.Setup(x => x.LockOwnedAsync(7, 3, It.IsAny<CancellationToken>())).ReturnsAsync(Conversation);
        Repository.Setup(x => x.GetOwnedAsync(7, 3, It.IsAny<CancellationToken>())).ReturnsAsync(Conversation);
        Repository.Setup(x => x.GetCompletedHistoryAsync(7, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AgentMessage>());
        Repository.Setup(x => x.NextSequenceAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(2);
        Repository.Setup(x => x.GetMessageAsync(7, It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Message);
        Repository.Setup(x => x.AddMessage(It.IsAny<AgentMessage>())).Callback<AgentMessage>(message => { if (message.ClientMessageId.HasValue) Message = message; });
        Repository.Setup(x => x.AddConversation(It.IsAny<AgentConversation>())).Callback<AgentConversation>(conversation => conversation.Id = 7);
        UnitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Transaction.Object);
        Llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));
    }

    /// <summary>Builds the real bounded orchestration with isolated catalog/provider boundaries.</summary>
    public AgentOrchestrator Orchestrator()
    {
        var catalog = AgentTestData.Validator(AgentTestData.Dish());
        var policy = new AgentStatePolicy();
        var tools = new AgentToolExecutor(catalog.Dishes.Object, catalog.Tags.Object,
            Mock.Of<ANGI.Application.Common.Interfaces.Services.Recommendation.IRecommendationService>(), policy,
            new(catalog.Routes.Object), Limits, Telemetry);
        return new(Llm.Object, tools, policy, new(), catalog.Validator, Limits, Telemetry);
    }

    /// <summary>Supplies a stable UTC clock for exact lease and expiry boundary assertions.</summary>
    private sealed class TestClock : TimeProvider
    {
        /// <summary>Returns the fixed business time for this isolated test suite.</summary>
        public override DateTimeOffset GetUtcNow() => new(Now);
    }
}
