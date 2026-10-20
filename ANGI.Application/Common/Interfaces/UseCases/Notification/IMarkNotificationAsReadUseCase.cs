using ANGI.Application.DTOs.Notification;

namespace ANGI.Application.Common.Interfaces.UseCases.Notification;

/// <summary>Coordinates marking notifications as read for the authenticated user (NOTI-03).</summary>
public interface IMarkNotificationAsReadUseCase
{
    Task<MarkNotificationAsReadResponseDto> ExecuteAsync(MarkNotificationAsReadRequestDto request, CancellationToken ct);
}
