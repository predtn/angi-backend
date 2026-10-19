using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Notification;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Notification;
using ANGI.Application.DTOs.Notification;
using FluentValidation;

namespace ANGI.Application.UseCases.Notification.MarkNotificationAsRead;

/// <summary>Coordinates marking notifications as read for the authenticated user (NOTI-03).</summary>
public sealed class MarkNotificationAsReadUseCase : IMarkNotificationAsReadUseCase
{
    private const string UnauthorizedCode = "UNAUTHORIZED";
    private const string UnauthorizedMessage = "Yêu cầu đăng nhập.";

    private readonly IValidator<MarkNotificationAsReadRequestDto> _validator;
    private readonly ICurrentUserService _currentUserService;
    private readonly INotificationRepository _notificationRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public MarkNotificationAsReadUseCase(
        IValidator<MarkNotificationAsReadRequestDto> validator,
        ICurrentUserService currentUserService,
        INotificationRepository notificationRepository,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _currentUserService = currentUserService;
        _notificationRepository = notificationRepository;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<MarkNotificationAsReadResponseDto> ExecuteAsync(
        MarkNotificationAsReadRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId;
        if (userId is null)
        {
            throw new UnauthorizedException(UnauthorizedCode, UnauthorizedMessage);
        }

        var markAll = request.All ?? false;
        var notifications = await _notificationRepository.GetUnreadNotificationsToMarkReadAsync(
            userId.Value,
            request.Ids,
            markAll,
            ct);

        if (notifications.Count > 0)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            foreach (var notification in notifications)
            {
                notification.ReadAt = now;
            }

            await _unitOfWork.SaveChangesAsync(ct);
        }

        return new MarkNotificationAsReadResponseDto
        {
            Updated = notifications.Count
        };
    }
}
