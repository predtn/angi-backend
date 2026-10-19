using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Notification;

/// <summary>Provides persistence operations for user notifications.</summary>
public interface INotificationRepository
{
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
