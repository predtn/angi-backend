namespace ANGI.Application.Common.Interfaces.Repositories.Notification;

/// <summary>Defines persistence operations for querying notifications.</summary>
public interface INotificationRepository
{
    /// <summary>Queries paginated notifications matching optional read-status and type filters.</summary>
    Task<(IReadOnlyList<Domain.Entities.Notification> Items, long TotalItems)> GetNotificationsAsync(
        int userId,
        bool? isRead,
        string? type,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>Counts unread notifications (where read_at is null) for the given user.</summary>
    Task<int> GetUnreadCountAsync(int userId, CancellationToken ct);
}
