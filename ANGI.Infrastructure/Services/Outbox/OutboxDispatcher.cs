using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ANGI.Infrastructure.Services.Outbox;

/// <summary>One pass of the outbox worker: claim a batch, send each message, settle it.</summary>
public sealed class OutboxDispatcher
{
    public static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);
    private const int MaxErrorLength = 2000;

    private readonly OutboxStore _store;
    private readonly IOutboxSender _sender;
    private readonly OutboxSettings _settings;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        OutboxStore store,
        IOutboxSender sender,
        IOptions<OutboxSettings> settings,
        ILogger<OutboxDispatcher> logger)
    {
        _store = store;
        _sender = sender;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>Delay before attempt number <paramref name="attempts"/> + 1: 5 s doubling, at most 15 min.</summary>
    public static TimeSpan Backoff(int attempts)
    {
        var seconds = FirstRetryDelay.TotalSeconds * Math.Pow(2, Math.Max(0, attempts - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelay.TotalSeconds));
    }

    /// <summary>Returns how many messages were claimed; a full batch means more may be waiting.</summary>
    public async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        var batchSize = Math.Max(1, _settings.BatchSize);
        // Messages are sent one after another, so the lock covers the whole batch.
        var lockFor = TimeSpan.FromSeconds(batchSize * Math.Max(1, _settings.TimeoutSeconds) + 30);
        var messages = await _store.ClaimAsync(batchSize, lockFor, ct);

        foreach (var message in messages)
        {
            var result = await _sender.SendAsync(message, ct);
            var attempts = (short)(message.AttemptCount + 1);
            var error = Truncate(result.Error);

            switch (result.Outcome)
            {
                case OutboxDeliveryOutcome.Delivered:
                    await _store.MarkDoneAsync(message.Id, attempts, ct);
                    break;
                case OutboxDeliveryOutcome.Retry when attempts < _settings.MaxAttempts:
                    var delay = result.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
                        ? (retryAfter < MaxRetryDelay ? retryAfter : MaxRetryDelay)
                        : Backoff(attempts);
                    await _store.ScheduleRetryAsync(message.Id, attempts, delay, error, ct);
                    _logger.LogWarning(
                        "Outbox {MessageId} {EventType} failed (attempt {Attempts}): {Error}; retry in {Delay}.",
                        message.Id, message.EventType, attempts, error, delay);
                    break;
                default:
                    await _store.MarkDeadAsync(message.Id, attempts, error, ct);
                    _logger.LogError(
                        "Outbox {MessageId} {EventType} is dead after {Attempts} attempt(s): {Error}.",
                        message.Id, message.EventType, attempts, error);
                    break;
            }
        }

        return messages.Count;
    }

    private static string? Truncate(string? error) =>
        error is { Length: > MaxErrorLength } ? error[..MaxErrorLength] : error;
}
