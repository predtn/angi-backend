namespace ANGI.Application.DTOs.Administration;

/// <summary>Request parameters for filtering and paging outbox messages.</summary>
public sealed class GetOutboxMessagesRequestDto
{
    /// <summary>Filter by outbox status (pending, processing, done, dead, failed; defaults to dead).</summary>
    public string? Status { get; set; }

    /// <summary>Filter by event type (DISH_UPSERTED, DISH_DEACTIVATED, FEEDBACK_CREATED).</summary>
    public string? EventType { get; set; }

    /// <summary>1-based page index (defaults to 1).</summary>
    public int Page { get; set; } = 1;

    /// <summary>Number of items per page (defaults to 20).</summary>
    public int PageSize { get; set; } = 20;
}

/// <summary>Detailed information about an outbox event message.</summary>
public sealed class OutboxMessageDto
{
    /// <summary>The internal record identifier in core.outbox_messages.</summary>
    public long Id { get; set; }

    /// <summary>The unique event UUID used as an Idempotency-Key when sending to external services.</summary>
    public Guid EventUuid { get; set; }

    /// <summary>The type of event (e.g. DISH_UPSERTED, DISH_DEACTIVATED, FEEDBACK_CREATED).</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>The partitioning / aggregate key ensuring ordered per-entity processing.</summary>
    public string AggregateKey { get; set; } = string.Empty;

    /// <summary>The JSON payload dispatched with the event.</summary>
    public object Payload { get; set; } = null!;

    /// <summary>Current processing status (pending, processing, done, dead).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Number of delivery attempts made.</summary>
    public int AttemptCount { get; set; }

    /// <summary>The error message returned from the most recent failed delivery attempt.</summary>
    public string? LastError { get; set; }

    /// <summary>The timestamp when the event was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>The timestamp when the event will next be retried, if applicable.</summary>
    public DateTime? NextAttemptAt { get; set; }
}
