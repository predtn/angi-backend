using ANGI.Domain.Entities;

namespace ANGI.Infrastructure.Services.Outbox;

/// <summary>What happens to a message after one attempt (project_architecture.md, Outbox).</summary>
public enum OutboxDeliveryOutcome
{
    /// <summary>2xx: done.</summary>
    Delivered,

    /// <summary>5xx, timeout or network error: try again later.</summary>
    Retry,

    /// <summary>4xx or an unreadable payload: sending it again cannot succeed.</summary>
    Dead
}

/// <summary>The outcome of one attempt; <see cref="RetryAfter"/> comes from reco's Retry-After header.</summary>
public sealed record OutboxDeliveryResult(OutboxDeliveryOutcome Outcome, string? Error = null, TimeSpan? RetryAfter = null)
{
    public static readonly OutboxDeliveryResult Delivered = new(OutboxDeliveryOutcome.Delivered);
}

/// <summary>Sends one outbox message to its reco endpoint.</summary>
public interface IOutboxSender
{
    /// <summary>Whether Recommendation:BaseUrl is set; without it the worker does not run.</summary>
    bool IsConfigured { get; }

    Task<OutboxDeliveryResult> SendAsync(OutboxMessage message, CancellationToken ct);
}
