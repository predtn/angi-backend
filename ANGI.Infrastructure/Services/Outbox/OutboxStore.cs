using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Persistences;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Services.Outbox;

/// <summary>
/// Claims and settles outbox rows. Only the outbox worker writes core.outbox_messages after the triggers. Every time
/// (locked_until, next_attempt_at, processed_at) comes from the database clock, the one the claim compares with.
/// </summary>
public sealed class OutboxStore
{
    private readonly ANGIContext _context;

    public OutboxStore(ANGIContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Locks up to <paramref name="batchSize"/> due rows for <paramref name="lockFor"/> and returns them in id order.
    /// A row is due when pending and past next_attempt_at, or processing with an expired lock (a worker died). Only the
    /// oldest unfinished row of each aggregate_key is claimed, so one dish's or one user's events go out in order even
    /// while an earlier one waits for a retry; SKIP LOCKED lets several workers share the table.
    /// </summary>
    public async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(int batchSize, TimeSpan lockFor, CancellationToken ct)
    {
        var lockSeconds = (int)Math.Ceiling(lockFor.TotalSeconds);
        var claimed = await _context.OutboxMessages
            .FromSqlInterpolated($"""
                UPDATE core.outbox_messages AS m
                SET status = 'processing', locked_until = now() + make_interval(secs => {lockSeconds})
                WHERE m.id IN (
                    SELECT o.id
                    FROM core.outbox_messages AS o
                    WHERE ((o.status = 'pending' AND o.next_attempt_at <= now())
                        OR (o.status = 'processing' AND o.locked_until < now()))
                      AND NOT EXISTS (
                          SELECT 1
                          FROM core.outbox_messages AS earlier
                          WHERE earlier.aggregate_key = o.aggregate_key
                            AND earlier.id < o.id
                            AND earlier.status IN ('pending', 'processing'))
                    ORDER BY o.id
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED)
                RETURNING m.*
                """)
            .AsNoTracking()
            .ToListAsync(ct);
        return claimed.OrderBy(message => message.Id).ToArray();
    }

    public Task MarkDoneAsync(long id, short attempts, CancellationToken ct) =>
        _context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE core.outbox_messages
            SET status = 'done', attempt_count = {attempts}, processed_at = now(), locked_until = NULL, last_error = NULL
            WHERE id = {id}
            """, ct);

    public Task ScheduleRetryAsync(long id, short attempts, TimeSpan delay, string? error, CancellationToken ct) =>
        _context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE core.outbox_messages
            SET status = 'pending', attempt_count = {attempts}, locked_until = NULL, last_error = {error},
                next_attempt_at = now() + make_interval(secs => {delay.TotalSeconds})
            WHERE id = {id}
            """, ct);

    public Task MarkDeadAsync(long id, short attempts, string? error, CancellationToken ct) =>
        _context.OutboxMessages.Where(message => message.Id == id).ExecuteUpdateAsync(set => set
            .SetProperty(message => message.Status, OutboxStatus.Dead)
            .SetProperty(message => message.AttemptCount, attempts)
            .SetProperty(message => message.LockedUntil, (DateTime?)null)
            .SetProperty(message => message.LastError, error), ct);
}
