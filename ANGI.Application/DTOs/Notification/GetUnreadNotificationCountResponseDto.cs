namespace ANGI.Application.DTOs.Notification;

/// <summary>Encapsulates the response data for NOTI-02 unread notification count badge.</summary>
public sealed class GetUnreadNotificationCountResponseDto
{
    public int UnreadCount { get; init; }
}
