using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Moderation.ClaimReport;

/// <summary>Implements MOD-RP3 with a row lock shared by report conclusions.</summary>
public sealed class ClaimReportUseCase(ModerationAccess access, IRestaurantModerationRepository repository,
    IUnitOfWork unitOfWork, ICloudinaryService cloudinary) : IClaimReportUseCase
{
    /// <summary>Assigns a pending report exactly once and commits only after the response can be mapped.</summary>
    public async Task<ReportDto> ExecuteAsync(long reportId, CancellationToken ct)
    {
        // STEP-01: Check the caller's permission through access.RequireAsync.
        await access.RequireAsync(ct, "REPORT_RESTAURANT_HANDLE", "REPORT_SOCIAL_HANDLE");
        // STEP-02: Prepare target from repository.GetReportTargetAsync.
        var target = await repository.GetReportTargetAsync(reportId, ct)
            ?? throw new NotFoundException("REPORT_NOT_FOUND", "Không tìm thấy báo cáo.");
        // STEP-03: Check the caller's permission through access.RequireAsync.
        var callerId = await access.RequireAsync(ct, target.Type == ReportTargetType.Restaurant ? "REPORT_RESTAURANT_HANDLE" : "REPORT_SOCIAL_HANDLE");
        // STEP-04: Open the database transaction.
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        // STEP-05: Prepare report from repository.LockReportAsync.
        var report = await repository.LockReportAsync(reportId, ct)
            ?? throw new NotFoundException("REPORT_NOT_FOUND", "Không tìm thấy báo cáo.");
        // STEP-06: Reject with REPORT_CLOSED when report.Status is one of the listed values.
        if (report.Status is ReportStatus.Resolved or ReportStatus.Dismissed)
            throw new ConflictException("REPORT_CLOSED", "Báo cáo đã được xử lý.");
        // STEP-07: Reject with REPORT_ALREADY_CLAIMED when report.Status is not ReportStatus.Pending or report.HandledBy has a value.
        if (report.Status != ReportStatus.Pending || report.HandledBy.HasValue)
            throw new ConflictException("REPORT_ALREADY_CLAIMED", "Báo cáo đã được nhận xử lý.");
        // STEP-08: Update report: Status and HandledBy.
        report.Status = ReportStatus.InReview;
        report.HandledBy = callerId;
        // STEP-09: Save the pending changes.
        await unitOfWork.SaveChangesAsync(ct);
        // STEP-10: Load display through repository.GetReportDisplayDataAsync.
        var display = await repository.GetReportDisplayDataAsync(target, [report], ct);
        // STEP-11: Prepare response through ModerationMapper.Report.
        var response = ModerationMapper.Report(report, display, cloudinary);
        // STEP-12: Commit the transaction.
        await transaction.CommitAsync(ct);
        // STEP-13: Return response.
        return response;
    }
}
