using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Notification;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Notification;
using ANGI.Application.DTOs.Notification;

namespace ANGI.Application.UseCases.Notification.GetUnreadNotificationCount;

/// <summary>Implements NOTI-02 by retrieving the unread notification badge count for the authenticated user.</summary>
public sealed class GetUnreadNotificationCountUseCase : IGetUnreadNotificationCountUseCase
{
    private readonly ICurrentUserService _currentUserService;
    private readonly INotificationRepository _notificationRepository;

    public GetUnreadNotificationCountUseCase(
        ICurrentUserService currentUserService,
        INotificationRepository notificationRepository)
    {
        _currentUserService = currentUserService;
        _notificationRepository = notificationRepository;
    }

    public async Task<GetUnreadNotificationCountResponseDto> ExecuteAsync(CancellationToken ct)
    {
        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xem số thông báo chưa đọc.");

        var unreadCount = await _notificationRepository.GetUnreadCountAsync(userId, ct);

        return new GetUnreadNotificationCountResponseDto
        {
            UnreadCount = unreadCount
        };
    }
}
