namespace ANGI.Infrastructure.Services.Roadmap;

/// <summary>RM-10 background worker settings (project_architecture.md, Configuration).</summary>
public sealed class RoadmapGenerationSettings
{
    public const string SectionName = "RoadmapGeneration";

    /// <summary>Wait between polls when no job is queued.</summary>
    public int PollSeconds { get; set; } = 2;
}
