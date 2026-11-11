using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Moderation.GetModRestaurant;

/// <summary>Implements MOD-R2 with full restaurant data and bounded report and audit history.</summary>
public sealed class GetModRestaurantUseCase(ModerationAccess access, IRestaurantModerationRepository repository,
    ICloudinaryService cloudinary, TimeProvider clock) : IGetModRestaurantUseCase
{
    /// <summary>Authorizes before loading a non-deleted restaurant and signing its private media.</summary>
    public async Task<ModRestaurantDetailDto> ExecuteAsync(long restaurantId, CancellationToken ct)
    {
        // STEP-01: Check the caller's permission through access.RequireAsync.
        await access.RequireAsync(ct, "RESTAURANT_MODERATE", "RESTAURANT_VERIFY");
        // STEP-02: Prepare restaurant, then load reports through repository.GetRecentReportsAsync, display through repository.GetReportDisplayDataAsync and more.
        var restaurant = await repository.GetDetailAsync(restaurantId, ct)
            ?? throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");
        var reports = await repository.GetRecentReportsAsync(restaurantId, ct);
        var display = await repository.GetReportDisplayDataAsync(new ReportTarget(ReportTargetType.Restaurant, restaurantId), reports, ct);
        var audit = await repository.GetRecentAuditAsync(restaurantId, ct);
        // STEP-03: Return the result of ModerationMapper.Detail.
        return ModerationMapper.Detail(restaurant, reports.Select(report => ModerationMapper.Report(report, display, cloudinary)).ToArray(),
            audit, cloudinary, clock);
    }
}
