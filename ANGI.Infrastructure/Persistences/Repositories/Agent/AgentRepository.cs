using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Models.Agent;
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

    public Task<AgentConversation?> GetByRoadmapAsync(long roadmapId, int userId, CancellationToken ct) =>
        context.AgentConversations.AsNoTracking().SingleOrDefaultAsync(x => x.RoadmapId == roadmapId && x.UserId == userId, ct);

    public async Task LockRoadmapConversationAsync(long roadmapId, CancellationToken ct) =>
        _ = await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('agent-roadmap:' || CAST({roadmapId} AS text), 0))", ct);

    public void AddOutboxMessage(OutboxMessage message) => context.OutboxMessages.Add(message);

    /// <summary>Retrieves the unique logical turn after the conversation has been locked.</summary>
    public Task<AgentMessage?> GetMessageAsync(long conversationId, long clientMessageId, CancellationToken ct) =>
        context.AgentMessages.FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.ClientMessageId == clientMessageId, ct);

    /// <summary>Finds the user message associated with an expired processing lease.</summary>
    public Task<AgentMessage?> GetTurnAsync(long conversationId, long turnId, CancellationToken ct) =>
        context.AgentMessages.FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.TurnId == turnId && x.Role == AgentMessageRole.User, ct);

    /// <summary>Allocates sequence numbers while the conversation lock is held.</summary>
    public async Task<int> NextSequenceAsync(long conversationId, CancellationToken ct)
    {
        var conversation = await context.AgentConversations.SingleAsync(x => x.Id == conversationId, ct);
        var stored = await context.AgentMessages.Where(x => x.ConversationId == conversationId).MaxAsync(x => (int?)x.SequenceNo, ct) ?? 0;
        return conversation.LastMessageSequence = checked(Math.Max(conversation.LastMessageSequence, stored) + 1);
    }

    /// <summary>Reads only completed turns, maintaining transcript order inside the bounded window.</summary>
    public async Task<AgentMessage[]> GetCompletedHistoryAsync(long conversationId, int limit, CancellationToken ct) =>
        (await context.AgentMessages.AsNoTracking().Where(x => x.ConversationId == conversationId &&
            context.AgentMessages.Any(user => user.ConversationId == conversationId && user.TurnId == x.TurnId &&
                user.Role == AgentMessageRole.User && user.ProcessingStatus == AgentProcessingStatus.Completed))
            .OrderByDescending(x => x.SequenceNo).Take(limit).ToArrayAsync(ct)).OrderBy(x => x.SequenceNo).ToArray();

    /// <summary>Tracks a new ordered transcript entry.</summary>
    public void AddMessage(AgentMessage message)
    {
        var conversation = context.AgentConversations.Local.SingleOrDefault(x => x.Id == message.ConversationId);
        if (conversation is not null) conversation.LastMessageSequence = Math.Max(conversation.LastMessageSequence, message.SequenceNo);
        context.AgentMessages.Add(message);
    }

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
    public async Task<int> NextProposalVersionAsync(long conversationId, CancellationToken ct)
    {
        var conversation = await context.AgentConversations.SingleAsync(x => x.Id == conversationId, ct);
        var stored = await context.AgentProposals.Where(x => x.ConversationId == conversationId).MaxAsync(x => (int?)x.ProposalVersion, ct) ?? 0;
        return conversation.LastProposalVersion = checked(Math.Max(conversation.LastProposalVersion, stored) + 1);
    }

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
              AND ((status = 'active' AND expires_at <= {now})
                OR (status <> 'active' AND updated_at <= {cutoff} AND
                    (roadmap_id IS NULL OR state <> jsonb_build_object() OR current_proposal_id IS NOT NULL))
                OR EXISTS (SELECT 1 FROM core.agent_messages m WHERE m.conversation_id = agent_conversations.id AND m.created_at < {cutoff}
                    AND NOT EXISTS (SELECT 1 FROM core.agent_messages u
                        CROSS JOIN LATERAL jsonb_array_elements_text(COALESCE(NULLIF(u.payload->'attempts', 'null'::jsonb), '[]'::jsonb)) a(value)
                        WHERE u.conversation_id = m.conversation_id AND u.turn_id = m.turn_id AND a.value::timestamptz >= {cutoff}))
                OR EXISTS (SELECT 1 FROM core.agent_proposals p WHERE p.conversation_id = agent_conversations.id
                    AND p.created_at < {cutoff} AND p.id IS DISTINCT FROM current_proposal_id
                    AND NOT EXISTS (SELECT 1 FROM core.agent_messages m WHERE m.conversation_id = p.conversation_id
                        AND m.response_payload->'proposal'->>'proposalId' = CAST(p.id AS text)
                        AND (m.created_at >= {cutoff} OR EXISTS (
                            SELECT 1 FROM jsonb_array_elements_text(COALESCE(NULLIF(m.payload->'attempts', 'null'::jsonb), '[]'::jsonb)) a(value)
                            WHERE a.value::timestamptz >= {cutoff})))))
            ORDER BY updated_at, id LIMIT 100 FOR UPDATE SKIP LOCKED
            """).ToArrayAsync(ct);

    public async Task PruneAsync(AgentConversation conversation, DateTime cutoff, CancellationToken ct)
    {
        // A recent retry extends replay retention for the whole logical turn, even when its original row is old.
        var messages = await context.AgentMessages.FromSqlInterpolated($"""
            SELECT m.* FROM core.agent_messages m
            WHERE m.conversation_id = {conversation.Id} AND m.created_at < {cutoff}
              AND NOT EXISTS (SELECT 1 FROM core.agent_messages u
                  CROSS JOIN LATERAL jsonb_array_elements_text(COALESCE(NULLIF(u.payload->'attempts', 'null'::jsonb), '[]'::jsonb)) a(value)
                  WHERE u.conversation_id = m.conversation_id AND u.turn_id = m.turn_id AND a.value::timestamptz >= {cutoff})
            """).ToArrayAsync(ct);
        var removableIds = messages.Select(x => x.Id).ToArray();
        var retainedResponses = await context.AgentMessages.AsNoTracking().Where(x => x.ConversationId == conversation.Id &&
            !removableIds.Contains(x.Id) && x.ResponsePayload != null).Select(x => x.ResponsePayload!).ToArrayAsync(ct);
        var replayProposals = retainedResponses.Select(x => AgentJson.Read<ANGI.Application.DTOs.Agent.AgentTurnDto>(x).Proposal?.ProposalId)
            .Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var proposals = await context.AgentProposals.Where(x => x.ConversationId == conversation.Id &&
            x.CreatedAt < cutoff && x.Id != conversation.CurrentProposalId && !replayProposals.Contains(x.Id)).ToArrayAsync(ct);
        conversation.LastMessageSequence = Math.Max(conversation.LastMessageSequence,
            messages.Select(x => x.SequenceNo).DefaultIfEmpty().Max());
        conversation.LastProposalVersion = Math.Max(conversation.LastProposalVersion,
            proposals.Select(x => x.ProposalVersion).DefaultIfEmpty().Max());
        conversation.PurgedClientMessageId = Math.Max(conversation.PurgedClientMessageId,
            messages.Select(x => x.ClientMessageId ?? 0).DefaultIfEmpty().Max());
        context.AgentMessages.RemoveRange(messages);
        context.AgentProposals.RemoveRange(proposals);
    }

    /// <summary>Deletes tracked children after the use case has removed the current-proposal pointer.</summary>
    public async Task RemoveConversationAsync(AgentConversation conversation, CancellationToken ct)
    {
        context.AgentMessages.RemoveRange(await context.AgentMessages.Where(x => x.ConversationId == conversation.Id).ToArrayAsync(ct));
        context.AgentProposals.RemoveRange(await context.AgentProposals.Where(x => x.ConversationId == conversation.Id).ToArrayAsync(ct));
        context.AgentConversations.Remove(conversation);
    }

    /// <summary>Reads the authoritative UTC timestamp after triggers have applied child changes.</summary>
    public Task<DateTime> GetRoadmapUpdatedAtAsync(long roadmapId, CancellationToken ct) =>
        context.Roadmaps.AsNoTracking().Where(x => x.Id == roadmapId).Select(x => x.UpdatedAt).SingleAsync(ct);

    /// <summary>Protects current restaurant and dish data through the final database commit.</summary>
    public async Task LockCatalogAsync(IReadOnlyCollection<int> dishIds, CancellationToken ct)
    {
        var ids = dishIds.Order().ToArray();
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM core.restaurants WHERE id IN (SELECT restaurant_id FROM core.dishes WHERE id = ANY({ids})) ORDER BY id FOR SHARE", ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM core.dishes WHERE id = ANY({ids}) ORDER BY id FOR SHARE", ct);
    }
}
