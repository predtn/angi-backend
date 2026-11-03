using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Agent;

/// <summary>Persists owned Agent resources; transaction boundaries belong to use cases.</summary>
public interface IAgentRepository
{
    /// <summary>Discards rolled-back tracked changes before releasing a failed attempt in a fresh transaction.</summary>
    void ResetTracking();
    /// <summary>Locks and refreshes an owned conversation for a short state transition.</summary>
    Task<AgentConversation?> LockOwnedAsync(long id, int userId, CancellationToken ct);
    /// <summary>Reads an owned conversation without extending its lifetime.</summary>
    Task<AgentConversation?> GetOwnedAsync(long id, int userId, CancellationToken ct);
    /// <summary>Serializes per-user quota checks across different conversations.</summary>
    Task LockUserAsync(int userId, CancellationToken ct);
    /// <summary>Counts user attempts admitted since the supplied UTC instant.</summary>
    Task<int> CountAttemptsAsync(int userId, DateTime since, CancellationToken ct);
    /// <summary>Tracks a new conversation in the current unit of work.</summary>
    void AddConversation(AgentConversation conversation);
    /// <summary>Reads the unique conversation attached to a roadmap after acquiring the user lock.</summary>
    Task<AgentConversation?> GetByRoadmapAsync(long roadmapId, int userId, CancellationToken ct);
    /// <summary>Serializes get-or-create without reversing the conversation/user lock order.</summary>
    Task LockRoadmapConversationAsync(long roadmapId, CancellationToken ct);
    /// <summary>Prunes expired payloads while maintaining durable identity high-water marks.</summary>
    Task PruneAsync(AgentConversation conversation, DateTime cutoff, CancellationToken ct);
    /// <summary>Queues a delivery in the current core transaction.</summary>
    void AddOutboxMessage(OutboxMessage message);
    /// <summary>Finds the logical user message under an already locked conversation.</summary>
    Task<AgentMessage?> GetMessageAsync(long conversationId, long clientMessageId, CancellationToken ct);
    /// <summary>Finds a previously processing turn while revoking an expired lease.</summary>
    Task<AgentMessage?> GetTurnAsync(long conversationId, long turnId, CancellationToken ct);
    /// <summary>Returns the next sequence under the conversation lock.</summary>
    Task<int> NextSequenceAsync(long conversationId, CancellationToken ct);
    /// <summary>Returns a bounded completed transcript without failed or partial output.</summary>
    Task<AgentMessage[]> GetCompletedHistoryAsync(long conversationId, int limit, CancellationToken ct);
    /// <summary>Tracks a user, assistant or tool transcript entry.</summary>
    void AddMessage(AgentMessage message);
    /// <summary>Returns a proposal only when it belongs to the checked conversation.</summary>
    Task<AgentProposal?> GetProposalAsync(long conversationId, long proposalId, CancellationToken ct);
    /// <summary>Reads an immutable proposal snapshot without adding it to the current change tracker.</summary>
    Task<AgentProposal?> GetProposalSnapshotAsync(long conversationId, long proposalId, CancellationToken ct);
    /// <summary>Allocates the next immutable proposal revision under the conversation lock.</summary>
    Task<int> NextProposalVersionAsync(long conversationId, CancellationToken ct);
    /// <summary>Tracks a new immutable proposal snapshot.</summary>
    void AddProposal(AgentProposal proposal);
    /// <summary>Reserves an identity so the final response and proposal can be persisted in one flush.</summary>
    Task<long> ReserveProposalIdAsync(CancellationToken ct);
    /// <summary>Locks a bounded retention batch while skipping live leases and other cleanup workers.</summary>
    Task<AgentConversation[]> LockCleanupBatchAsync(DateTime now, DateTime cutoff, CancellationToken ct);
    /// <summary>Removes children and the unlinked conversation without touching official roadmaps.</summary>
    Task RemoveConversationAsync(AgentConversation conversation, CancellationToken ct);
    /// <summary>Reads the exact roadmap timestamp after all database-managed child updates.</summary>
    Task<DateTime> GetRoadmapUpdatedAtAsync(long roadmapId, CancellationToken ct);
    /// <summary>Locks catalog rows in stable order so confirmation prices and availability remain atomic.</summary>
    Task LockCatalogAsync(IReadOnlyCollection<int> dishIds, CancellationToken ct);
}
