using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Roadmap;

/// <summary>Persistence of RM-10 / RM-11 generation jobs and of the background job run.</summary>
public interface IRoadmapGenerationRepository
{
    /// <summary>Whether the roadmap has a queued or running job (409 GENERATION_IN_PROGRESS).</summary>
    Task<bool> HasActiveJobAsync(long roadmapId, CancellationToken ct);

    /// <summary>Adds a queued job to the current unit of work.</summary>
    void AddJob(RoadmapGenerationJob job);

    /// <summary>Loads a job of an owned, visible roadmap for RM-11 (read-only).</summary>
    Task<RoadmapGenerationJob?> GetOwnedJobAsync(long roadmapId, long jobId, int userId, CancellationToken ct);

    /// <summary>Puts jobs left running by a stopped host back in the queue.</summary>
    Task RequeueInterruptedAsync(CancellationToken ct);

    /// <summary>Atomically marks the oldest queued job running and returns its id, or null when none waits.</summary>
    Task<long?> ClaimNextJobAsync(CancellationToken ct);

    /// <summary>Loads a job with its roadmap (deleted ones included), days and items, read-only, to plan the run.</summary>
    Task<RoadmapGenerationJob?> GetJobSnapshotAsync(long jobId, CancellationToken ct);

    /// <summary>Loads the job tracked, to record the result.</summary>
    Task<RoadmapGenerationJob?> GetTrackedJobAsync(long jobId, CancellationToken ct);

    /// <summary>Marks the job failed with the message and the database time, outside the change tracker.</summary>
    Task MarkFailedAsync(long jobId, string errorMessage, CancellationToken ct);

    /// <summary>Adds the ROADMAP_GENERATED notification to the current unit of work.</summary>
    void AddNotification(Domain.Entities.Notification notification);
}
