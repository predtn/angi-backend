namespace ANGI.Application.DTOs.Roadmap;

/// <summary>Response data of RM-11.</summary>
public sealed class RoadmapGenerationJobDto
{
    public long JobId { get; init; }

    /// <summary>full | day | slot.</summary>
    public string Scope { get; init; } = string.Empty;

    /// <summary>queued | running | succeeded | failed.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Set when the job failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>roadmap_items.id the job created; empty until it finishes (read from raw_output).</summary>
    public IReadOnlyList<long> CreatedItemIds { get; init; } = [];

    public DateTime CreatedAt { get; init; }

    public DateTime? FinishedAt { get; init; }
}
