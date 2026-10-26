using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Agent;

/// <summary>Implements durable Agent state with PostgreSQL row locks and ownership-safe lookups.</summary>
public sealed class AgentRepository(ANGIContext context) : IAgentRepository
{
    /// <summary>Prevents rolled-back proposal changes from being flushed by failure recovery.</summary>
    public void ResetTracking() => context.ChangeTracker.Clear();
    /// <summary>Locks and reloads tracked state so late attempts cannot observe a stale lease.</summary>
    public async Task<AgentConversation?> LockOwnedAsync(long id, int userId, CancellationToken ct)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM core.agent_conversations WHERE id = {id} AND user_id = {userId} FOR UPDATE", ct);
        var tracked = context.AgentConversations.Local.FirstOrDefault(x => x.Id == id);
        if (tracked is not null) await context.Entry(tracked).ReloadAsync(ct);
        return await context.AgentConversations.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
    }

    /// <summary>Reads only the requested owner's conversation.</summary>
    public Task<AgentConversation?> GetOwnedAsync(long id, int userId, CancellationToken ct) =>
        context.AgentConversations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);

    /// <summary>Serializes quota admission for all conversations owned by this user.</summary>
    public Task LockUserAsync(int userId, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM core.users WHERE id = {userId} FOR UPDATE", ct);

    /// <summary>Counts every admitted attempt, including retries of failed turns.</summary>
    public Task<int> CountAttemptsAsync(int userId, DateTime since, CancellationToken ct) =>
        context.Database.SqlQuery<int>($"""
            SELECT count(*)::integer AS "Value"
            FROM core.agent_messages m JOIN core.agent_conversations c ON c.id = m.conversation_id
            CROSS JOIN LATERAL jsonb_array_elements_text(m.payload->'attempts') a(value)
            WHERE c.user_id = {userId} AND m.role = 'user' AND a.value::timestamptz >= {since}
            """).SingleAsync(ct);

    /// <summary>Tracks a new conversation.</summary>
    public void AddConversation(AgentConversation conversation) => context.AgentConversations.Add(conversation);

    /// <summary>Retrieves the unique logical turn after the conversation has been locked.</summary>
    public Task<AgentMessage?> GetMessageAsync(long conversationId, long clientMessageId, CancellationToken ct) =>
        context.AgentMessages.FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.ClientMessageId == clientMessageId, ct);

    /// <summary>Finds the user message associated with an expired processing lease.</summary>
    public Task<AgentMessage?> GetTurnAsync(long conversationId, long turnId, CancellationToken ct) =>
        context.AgentMessages.FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.TurnId == turnId && x.Role == AgentMessageRole.User, ct);

    /// <summary>Allocates sequence numbers while the conversation lock is held.</summary>
    public async Task<int> NextSequenceAsync(long conversationId, CancellationToken ct) =>
        (await context.AgentMessages.Where(x => x.ConversationId == conversationId).MaxAsync(x => (int?)x.SequenceNo, ct) ?? 0) + 1;

    /// <summary>Reads only completed turns, maintaining transcript order inside the bounded window.</summary>
    public async Task<AgentMessage[]> GetCompletedHistoryAsync(long conversationId, int limit, CancellationToken ct) =>
        (await context.AgentMessages.AsNoTracking().Where(x => x.ConversationId == conversationId &&
            context.AgentMessages.Any(user => user.ConversationId == conversationId && user.TurnId == x.TurnId &&
                user.Role == AgentMessageRole.User && user.ProcessingStatus == AgentProcessingStatus.Completed))
            .OrderByDescending(x => x.SequenceNo).Take(limit).ToArrayAsync(ct)).OrderBy(x => x.SequenceNo).ToArray();

    /// <summary>Tracks a new ordered transcript entry.</summary>
    public void AddMessage(AgentMessage message) => context.AgentMessages.Add(message);

    /// <summary>Restricts proposal access to the already checked conversation.</summary>
    public async Task<AgentProposal?> GetProposalAsync(long conversationId, long proposalId, CancellationToken ct)
    {
        var tracked = context.AgentProposals.Local.FirstOrDefault(x => x.Id == proposalId);
        if (tracked is not null) await context.Entry(tracked).ReloadAsync(ct);
        return await context.AgentProposals.FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.Id == proposalId, ct);
    }

    /// <summary>Reads an immutable proposal for context loading and GET without tracking or mutating it.</summary>
    public Task<AgentProposal?> GetProposalSnapshotAsync(long conversationId, long proposalId, CancellationToken ct) =>
        context.AgentProposals.AsNoTracking().FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.Id == proposalId, ct);

    /// <summary>Allocates a conversation-local proposal version while holding the conversation lock.</summary>
    public async Task<int> NextProposalVersionAsync(long conversationId, CancellationToken ct) =>
        (await context.AgentProposals.Where(x => x.ConversationId == conversationId).MaxAsync(x => (int?)x.ProposalVersion, ct) ?? 0) + 1;

    /// <summary>Tracks a new immutable snapshot.</summary>
    public void AddProposal(AgentProposal proposal) => context.AgentProposals.Add(proposal);

    /// <summary>Uses the database identity sequence without a separate snapshot write.</summary>
    public Task<long> ReserveProposalIdAsync(CancellationToken ct) => context.Database.SqlQueryRaw<long>(
        "SELECT nextval(pg_get_serial_sequence('core.agent_proposals', 'id')) AS \"Value\"").SingleAsync(ct);

    /// <summary>Claims at most 100 inactive conversations without waiting on other workers or requests.</summary>
    public Task<AgentConversation[]> LockCleanupBatchAsync(DateTime now, DateTime cutoff, CancellationToken ct) =>
        context.AgentConversations.FromSqlInterpolated($"""
            SELECT * FROM core.agent_conversations
            WHERE (processing_until IS NULL OR processing_until <= {now})
              AND ((status = 'active' AND expires_at <= {now}) OR (status <> 'active' AND updated_at <= {cutoff}))
            ORDER BY updated_at, id LIMIT 100 FOR UPDATE SKIP LOCKED
            """).ToArrayAsync(ct);

    /// <summary>Deletes tracked children after the use case has removed the current-proposal pointer.</summary>
    public async Task RemoveConversationAsync(AgentConversation conversation, CancellationToken ct)
    {
        context.AgentMessages.RemoveRange(await context.AgentMessages.Where(x => x.ConversationId == conversation.Id).ToArrayAsync(ct));
        context.AgentProposals.RemoveRange(await context.AgentProposals.Where(x => x.ConversationId == conversation.Id).ToArrayAsync(ct));
        context.AgentConversations.Remove(conversation);
    }

    /// <summary>Reads the authoritative database revision after triggers have applied child changes.</summary>
    public Task<long> GetRoadmapVersionAsync(long roadmapId, CancellationToken ct) =>
        context.Roadmaps.AsNoTracking().Where(x => x.Id == roadmapId).Select(x => x.Version).SingleAsync(ct);

    /// <summary>Protects current restaurant and dish data through the final database commit.</summary>
    public async Task LockCatalogAsync(IReadOnlyCollection<int> dishIds, CancellationToken ct)
    {
        var ids = dishIds.Order().ToArray();
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM core.restaurants WHERE id IN (SELECT restaurant_id FROM core.dishes WHERE id = ANY({ids})) ORDER BY id FOR SHARE", ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM core.dishes WHERE id = ANY({ids}) ORDER BY id FOR SHARE", ct);
    }
}
