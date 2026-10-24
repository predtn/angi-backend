using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Roadmap;

/// <summary>EF Core persistence of RM-10 / RM-11 generation jobs and their background run.</summary>
public sealed class RoadmapGenerationRepository : IRoadmapGenerationRepository
{
    private readonly ANGIContext _context;

    /// <summary>Initializes job persistence with the scoped database context.</summary>
    public RoadmapGenerationRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public Task<bool> HasActiveJobAsync(long roadmapId, CancellationToken ct) =>
        _context.RoadmapGenerationJobs.AnyAsync(
            job => job.RoadmapId == roadmapId &&
                   (job.Status == GenerationJobStatus.Queued || job.Status == GenerationJobStatus.Running),
            ct);

    /// <inheritdoc />
    public void AddJob(RoadmapGenerationJob job)
    {
        _context.RoadmapGenerationJobs.Add(job);
    }

    /// <inheritdoc />
    public Task<RoadmapGenerationJob?> GetOwnedJobAsync(long roadmapId, long jobId, int userId, CancellationToken ct) =>
        _context.RoadmapGenerationJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                job => job.Id == jobId &&
                       job.RoadmapId == roadmapId &&
                       job.Roadmap.UserId == userId &&
                       job.Roadmap.DeletedAt == null,
                ct);

    /// <inheritdoc />
    public Task RequeueInterruptedAsync(CancellationToken ct) =>
        _context.Database.ExecuteSqlRawAsync(
            "UPDATE core.roadmap_generation_jobs SET status = 'queued' WHERE status = 'running'", ct);

    /// <inheritdoc />
    public async Task<long?> ClaimNextJobAsync(CancellationToken ct)
    {
        // SKIP LOCKED: a job is claimed once even if another worker polls at the same time.
        var claimed = await _context.Database
            .SqlQuery<long>($"""
                UPDATE core.roadmap_generation_jobs AS job
                SET status = 'running'
                WHERE job.id = (
                    SELECT queued.id
                    FROM core.roadmap_generation_jobs AS queued
                    WHERE queued.status = 'queued'
                    ORDER BY queued.id
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED)
                RETURNING job.id AS "Value"
                """)
            .ToListAsync(ct);
        return claimed.Count == 0 ? null : claimed[0];
    }

    /// <inheritdoc />
    public Task<RoadmapGenerationJob?> GetJobSnapshotAsync(long jobId, CancellationToken ct) =>
        _context.RoadmapGenerationJobs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(job => job.Roadmap)
                .ThenInclude(roadmap => roadmap.Days)
                    .ThenInclude(day => day.Items)
            .AsSplitQuery()
            .FirstOrDefaultAsync(job => job.Id == jobId, ct);

    /// <inheritdoc />
    public Task<RoadmapGenerationJob?> GetTrackedJobAsync(long jobId, CancellationToken ct) =>
        _context.RoadmapGenerationJobs.FirstOrDefaultAsync(job => job.Id == jobId, ct);

    /// <inheritdoc />
    public Task MarkFailedAsync(long jobId, string errorMessage, CancellationToken ct) =>
        _context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE core.roadmap_generation_jobs
            SET status = 'failed', error_message = {errorMessage}, finished_at = now()
            WHERE id = {jobId}
            """, ct);

    /// <inheritdoc />
    public void AddNotification(Domain.Entities.Notification notification)
    {
        _context.Notifications.Add(notification);
    }
}
