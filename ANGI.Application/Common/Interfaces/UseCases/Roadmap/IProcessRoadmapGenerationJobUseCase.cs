namespace ANGI.Application.Common.Interfaces.UseCases.Roadmap;

/// <summary>Runs one claimed RM-10 job in the background worker.</summary>
public interface IProcessRoadmapGenerationJobUseCase
{
    /// <summary>
    /// Plans and writes the items, then marks the job succeeded; an expected problem marks it failed with a message.
    /// An unexpected error marks it failed too and is rethrown for the worker to log.
    /// </summary>
    Task ExecuteAsync(long jobId, CancellationToken ct);
}
