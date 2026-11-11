namespace ANGI.Application.DTOs.Notification;

/// <summary>Encapsulates query parameters for NOTI-01 notification listing.</summary>
public sealed class GetNotificationsRequestDto
{
    public bool? IsRead { get; init; }
    public string? Type { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
