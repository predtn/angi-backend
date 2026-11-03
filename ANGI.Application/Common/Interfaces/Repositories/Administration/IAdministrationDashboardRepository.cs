using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.Common.Interfaces.Repositories.Administration;

/// <summary>Provides optimized analytical and statistical queries for the administrative dashboard.</summary>
public interface IAdministrationDashboardRepository
{
    /// <summary>Retrieves aggregated summary statistics for accounts, restaurants, queues, content, roadmaps, feedback, and outbox.</summary>
    Task<DashboardSummaryDto> GetSummaryAsync(DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Retrieves time-series data grouped chronologically by bucket interval for the requested metric.</summary>
    Task<IReadOnlyList<DashboardTimeSeriesPointDto>> GetTimeSeriesAsync(string metric, DateOnly from, DateOnly to, string interval, CancellationToken ct);

    /// <summary>Retrieves top ranking items (dishes, restaurants, or blogs) ordered descending by score.</summary>
    Task<IReadOnlyList<DashboardTopItemDto>> GetTopAsync(string type, int limit, CancellationToken ct);
}
