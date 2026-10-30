using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.Services.Agent;

/// <summary>Persists committed impressions atomically; the outbox worker sends them after commit.</summary>
public static class AgentCommittedImpressionReporter
{
    private static readonly JsonSerializerOptions SnakeCase = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static void Enqueue(IAgentRepository repository, AgentSnapshot snapshot, long proposalId, int userId, DateTime committedAt)
    {
        foreach (var group in snapshot.Items.Where(x => x.RequestId.HasValue).GroupBy(x => x.RequestId!.Value))
            foreach (var batch in group.Select(x => x.DishId).Distinct().Chunk(200))
                repository.AddOutboxMessage(new OutboxMessage
                {
                    EventType = OutboxEventType.AgentImpressionRecorded,
                    AggregateKey = $"user:{userId}",
                    Payload = JsonSerializer.Serialize(new { UserId = userId, RequestId = group.Key,
                        DishIds = batch, ShownAt = committedAt, AgentProposalId = proposalId }, SnakeCase),
                    Status = OutboxStatus.Pending
                });
    }
}
