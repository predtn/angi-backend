using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Models.Audit;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Moderation;

/// <summary>Shares restaurant state changes, audit records and owner notifications across MOD-R3 and MOD-RP4.</summary>
public sealed class RestaurantModerationEffects(IRestaurantModerationRepository repository, IAuditLogService audit)
{
    /// <summary>Changes restaurant visibility and stages the matching audit and owner notification once.</summary>
    public void Apply(Domain.Entities.Restaurant restaurant, RestaurantModerationStatus status, string? reason, long? reportId)
    {
        if (restaurant.ModerationStatus == status) return;
        var oldStatus = restaurant.ModerationStatus;
        restaurant.ModerationStatus = status;
        var action = status switch
        {
            RestaurantModerationStatus.Visible => AuditActions.RestaurantShown,
            RestaurantModerationStatus.Hidden => AuditActions.RestaurantHidden,
            _ => AuditActions.RestaurantSuspended
        };
        audit.Add(new AuditLogEntry
        {
            Action = action, EntityType = AuditEntityTypes.Restaurant, EntityId = restaurant.Id,
            SubjectUserId = restaurant.OwnerId, OldValues = new { ModerationStatus = oldStatus },
            NewValues = new { ModerationStatus = status, Reason = reason }
        });
        repository.AddNotification(new Domain.Entities.Notification
        {
            RecipientId = restaurant.OwnerId, Type = action, Title = "Trạng thái kiểm duyệt nhà hàng đã thay đổi",
            Body = reason, Data = JsonSerializer.Serialize(new { restaurant_id = restaurant.Id, report_id = reportId })
        });
    }
}
