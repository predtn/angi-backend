using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Social;

/// <summary>Provides EF Core reads and writes for reports.</summary>
public sealed class ReportRepository : IReportRepository
{
    private readonly ANGIContext _context;

    /// <summary>Initializes report persistence with the application context.</summary>
    public ReportRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public Task<bool> HasPendingReportAsync(
        int reporterId,
        ReportTargetType targetType,
        long targetId,
        CancellationToken ct)
    {
        return _context.Reports
            .AsNoTracking()
            .AnyAsync(r => r.ReporterId == reporterId
                        && r.TargetType == targetType
                        && (targetType == ReportTargetType.Blog ? r.BlogId == targetId : r.RestaurantId == targetId)
                        && (r.Status == ReportStatus.Pending || r.Status == ReportStatus.InReview), ct);
    }

    /// <inheritdoc />
    public Task<bool> ExistsPendingReportAsync(int reporterId, long blogId, CancellationToken ct)
    {
        return HasPendingReportAsync(reporterId, ReportTargetType.Blog, blogId, ct);
    }

    /// <inheritdoc />
    public void Add(Report report)
    {
        _context.Reports.Add(report);
    }
}
