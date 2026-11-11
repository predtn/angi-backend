namespace ANGI.Application.DTOs.Notification;

/// <summary>Response payload indicating the number of notifications marked as read (NOTI-03).</summary>
public sealed class MarkNotificationAsReadResponseDto
{
    /// <summary>The count of notifications successfully marked as read.</summary>
    public int Updated { get; set; }
}
