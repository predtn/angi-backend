namespace ANGI.Application.Common.Interfaces.Repositories.Notification;

/// <summary>Defines persistence operations for user notifications.</summary>
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

    /// <summary>
    /// Retrieves unread notifications belonging to the user that should be marked as read.
    /// If markAll is true, returns all unread notifications of the user.
    /// Otherwise, returns only unread notifications whose IDs are in the provided list.
    /// Notifications not belonging to the user or already read are ignored.
    /// </summary>
    Task<List<Domain.Entities.Notification>> GetUnreadNotificationsToMarkReadAsync(
        int userId,
        IReadOnlyList<long>? ids,
        bool markAll,
        CancellationToken ct);
}
