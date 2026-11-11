namespace ANGI.Application.DTOs.Notification;

/// <summary>Encapsulates the response data for NOTI-01 notification listing.</summary>
public sealed class GetNotificationsResponseDto
{
    public IReadOnlyList<NotificationDto> Items { get; init; } = Array.Empty<NotificationDto>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public long TotalItems { get; init; }
    public int TotalPages { get; init; }
    public int UnreadCount { get; init; }
}
