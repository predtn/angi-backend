namespace ANGI.Application.DTOs.Notification;

/// <summary>Request payload for marking notifications as read (NOTI-03).</summary>
public sealed class MarkNotificationAsReadRequestDto
{
    /// <summary>IDs of specific notifications to mark as read (up to 100).</summary>
    public List<long>? Ids { get; set; }

    /// <summary>When true, marks all unread notifications of the user as read.</summary>
    public bool? All { get; set; }
}
