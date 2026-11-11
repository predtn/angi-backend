namespace ANGI.Application.DTOs.Notification;

/// <summary>Represents a single notification item for the authenticated user.</summary>
public sealed class NotificationDto
{
    public long Id { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Body { get; init; }
    public object Data { get; init; } = default!;
    public DateTime? ReadAt { get; init; }
    public DateTime CreatedAt { get; init; }
}
