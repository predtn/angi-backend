using ANGI.Application.DTOs.Notification;

namespace ANGI.Application.Common.Interfaces.UseCases.Notification;

/// <summary>Defines the contract for retrieving a paginated list of user notifications (NOTI-01).</summary>
public interface IGetNotificationsUseCase
{
    Task<GetNotificationsResponseDto> ExecuteAsync(GetNotificationsRequestDto request, CancellationToken ct);
}
