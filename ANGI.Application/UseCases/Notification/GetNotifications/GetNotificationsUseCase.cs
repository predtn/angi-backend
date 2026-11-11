using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Notification;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Notification;
using ANGI.Application.DTOs.Notification;
using FluentValidation;

namespace ANGI.Application.UseCases.Notification.GetNotifications;

/// <summary>Implements NOTI-01 by retrieving paginated notifications and total unread count for the authenticated user.</summary>
public sealed class GetNotificationsUseCase : IGetNotificationsUseCase
{
    private readonly IValidator<GetNotificationsRequestDto> _validator;
    private readonly ICurrentUserService _currentUserService;
    private readonly INotificationRepository _notificationRepository;

    public GetNotificationsUseCase(
        IValidator<GetNotificationsRequestDto> validator,
        ICurrentUserService currentUserService,
        INotificationRepository notificationRepository)
    {
        _validator = validator;
        _currentUserService = currentUserService;
        _notificationRepository = notificationRepository;
    }

    public async Task<GetNotificationsResponseDto> ExecuteAsync(
        GetNotificationsRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xem thông báo.");

        var (items, totalItems) = await _notificationRepository.GetNotificationsAsync(
            userId,
            request.IsRead,
            request.Type,
            request.Page,
            request.PageSize,
            ct);

        var unreadCount = await _notificationRepository.GetUnreadCountAsync(userId, ct);

        var totalPages = (int)Math.Ceiling(totalItems / (double)request.PageSize);

        return new GetNotificationsResponseDto
        {
            Items = items.Select(NotificationMapper.Map).ToList(),
            Page = request.Page,
            PageSize = request.PageSize,
            TotalItems = totalItems,
            TotalPages = totalPages,
            UnreadCount = unreadCount
        };
    }
}
