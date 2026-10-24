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
        await access.RequireAsync(ct, "REPORT_RESTAURANT_HANDLE", "REPORT_SOCIAL_HANDLE");
        var target = await repository.GetReportTargetAsync(reportId, ct)
            ?? throw new NotFoundException("REPORT_NOT_FOUND", "Không tìm thấy báo cáo.");
        var callerId = await access.RequireAsync(ct, target.Type == ReportTargetType.Restaurant ? "REPORT_RESTAURANT_HANDLE" : "REPORT_SOCIAL_HANDLE");
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var report = await repository.LockReportAsync(reportId, ct)
            ?? throw new NotFoundException("REPORT_NOT_FOUND", "Không tìm thấy báo cáo.");
        if (report.Status is ReportStatus.Resolved or ReportStatus.Dismissed)
            throw new ConflictException("REPORT_CLOSED", "Báo cáo đã được xử lý.");
        if (report.Status == ReportStatus.InReview)
            throw new ConflictException("REPORT_ALREADY_CLAIMED", "Báo cáo đã được nhận xử lý.");
        report.Status = ReportStatus.InReview;
        report.HandledBy = callerId;
        await unitOfWork.SaveChangesAsync(ct);
        var display = await repository.GetReportDisplayDataAsync(target, [report], ct);
        var response = ModerationMapper.Report(report, display, cloudinary);
        await transaction.CommitAsync(ct);
        return response;
    }
}
