using ANGI.Application.DTOs.Notification;

namespace ANGI.Application.Common.Interfaces.UseCases.Notification;

/// <summary>Defines the contract for retrieving the unread notification badge count (NOTI-02).</summary>
public interface IGetUnreadNotificationCountUseCase
{
    Task<GetUnreadNotificationCountResponseDto> ExecuteAsync(CancellationToken ct);
}
