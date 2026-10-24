using ANGI.Application.Common.Models.Roadmap;

namespace ANGI.Application.Common.Interfaces.Services;

/// <summary>AI provider that schedules one roadmap day from its candidate cluster (RM-10 step 5).</summary>
public interface IRoadmapPlannerService
{
    /// <summary>Version of the prompt, stored in roadmap_generation_jobs.prompt_version.</summary>
    string PromptVersion { get; }

    /// <summary>
    /// Returns the AI's schedule, or null when the provider is not configured, fails, times out or answers in an
    /// unreadable shape: the job then plans the day itself. Only the caller's cancellation is thrown.
    /// </summary>
    Task<RoadmapPlanResult?> PlanDayAsync(RoadmapPlanRequest request, CancellationToken ct);
}
