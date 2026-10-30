using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Services.Agent;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent;

public sealed class AgentCommittedImpressionReporterTests
{
    [Fact]
    public void Enqueue_GroupsDistinctDishesByRequestAndSkipsFallback()
    {
        var request = Guid.NewGuid();
        var repository = new Mock<IAgentRepository>();
        var events = new List<OutboxMessage>();
        repository.Setup(x => x.AddOutboxMessage(It.IsAny<OutboxMessage>())).Callback<OutboxMessage>(events.Add);
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item() with { RequestId = request },
            AgentTestData.Item() with { ItemKey = "second", RequestId = request },
            AgentTestData.Item() with { ItemKey = "fallback", DishId = 2, RequestId = null });
        AgentCommittedImpressionReporter.Enqueue(repository.Object, snapshot, 9, 3, DateTime.UtcNow);
        var message = events.Should().ContainSingle().Subject;
        message.EventType.Should().Be(OutboxEventType.AgentImpressionRecorded);
        using var payload = JsonDocument.Parse(message.Payload);
        payload.RootElement.GetProperty("agent_proposal_id").GetInt64().Should().Be(9);
        payload.RootElement.GetProperty("dish_ids").GetArrayLength().Should().Be(1);
    }
}
