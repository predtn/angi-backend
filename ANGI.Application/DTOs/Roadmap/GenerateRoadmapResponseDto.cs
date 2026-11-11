namespace ANGI.Application.DTOs.Roadmap;

/// <summary>Response data of RM-10 (202 Accepted).</summary>
public sealed class GenerateRoadmapResponseDto
{
    /// <summary>roadmap_generation_jobs.id.</summary>
    public long JobId { get; init; }

    /// <summary>Always queued.</summary>
    public string Status { get; init; } = string.Empty;
}
