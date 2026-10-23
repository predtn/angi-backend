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
        await access.RequireAsync(ct, "RESTAURANT_MODERATE", "RESTAURANT_VERIFY");
        var restaurant = await repository.GetDetailAsync(restaurantId, ct)
            ?? throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");
        var reports = await repository.GetRecentReportsAsync(restaurantId, ct);
        var display = await repository.GetReportDisplayDataAsync(new ReportTarget(ReportTargetType.Restaurant, restaurantId), reports, ct);
        var audit = await repository.GetRecentAuditAsync(restaurantId, ct);
        return ModerationMapper.Detail(restaurant, reports.Select(report => ModerationMapper.Report(report, display, cloudinary)).ToArray(),
            audit, cloudinary, clock);
    }
}
