using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.Common.Interfaces.Repositories.Social;

/// <summary>Defines persistence operations for reports.</summary>
public interface IReportRepository
{
    /// <summary>Checks whether the user has a pending or in-review report on the target.</summary>
    Task<bool> HasPendingReportAsync(int reporterId, ReportTargetType targetType, long targetId, CancellationToken ct);

    /// <summary>Adds a new report aggregate.</summary>
    void Add(Report report);
}
