using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Moderation.GetReport;

/// <summary>Implements MOD-RP2 with a target-specific permission check before loading report content.</summary>
public sealed class GetReportUseCase(ModerationAccess access, IRestaurantModerationRepository repository,
    ICloudinaryService cloudinary, TimeProvider clock) : IGetReportUseCase
{
    /// <summary>Loads the authorized report, its full target and only other open reports on that target.</summary>
    public async Task<ReportDetailDto> ExecuteAsync(long reportId, CancellationToken ct)
    {
        // STEP-01: Check the caller's permission through access.RequireAsync.
        await access.RequireAsync(ct, "REPORT_RESTAURANT_HANDLE", "REPORT_SOCIAL_HANDLE");
        // STEP-02: Prepare target from repository.GetReportTargetAsync.
        var target = await repository.GetReportTargetAsync(reportId, ct)
            ?? throw new NotFoundException("REPORT_NOT_FOUND", "Không tìm thấy báo cáo.");
        // STEP-03: Check the caller's permission through access.RequireAsync.
        var callerId = await access.RequireAsync(ct, target.Type == ReportTargetType.Restaurant ? "REPORT_RESTAURANT_HANDLE" : "REPORT_SOCIAL_HANDLE");
        // STEP-04: Prepare report from repository.GetReportAsync and content.
        var report = await repository.GetReportAsync(reportId, ct)
            ?? throw new NotFoundException("REPORT_NOT_FOUND", "Không tìm thấy báo cáo.");
        object content;
        // STEP-05: When target.Type is ReportTargetType.Restaurant, prepare restaurant from repository.GetReportRestaurantAsync and more.
        if (target.Type == ReportTargetType.Restaurant)
        {
            var restaurant = await repository.GetReportRestaurantAsync(target.Id, ct)
                ?? throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");
            content = ModerationMapper.ReportRestaurant(restaurant, clock);
        }
        else
        {
            var blog = await repository.GetReportBlogAsync(target.Id, callerId, ct)
                ?? throw new NotFoundException("BLOG_NOT_FOUND", "Không tìm thấy bài viết.");
            content = ModerationMapper.ReportBlog(blog, callerId);
        }
        // STEP-06: Load related through repository.GetRelatedReportsAsync and display through repository.GetReportDisplayDataAsync.
        var related = await repository.GetRelatedReportsAsync(target, reportId, ct);
        var display = await repository.GetReportDisplayDataAsync(target, [report, .. related], ct);
        // STEP-07: Return the result of ModerationMapper.ReportDetail.
        return ModerationMapper.ReportDetail(report, display, cloudinary, content,
            related.Select(item => ModerationMapper.Report(item, display, cloudinary)).ToArray());
    }
}
