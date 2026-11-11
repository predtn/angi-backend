using ANGI.Application.Common.Interfaces.UseCases.Notification;
using ANGI.Application.UseCases.Notification.GetNotifications;
using ANGI.Application.UseCases.Notification.GetUnreadNotificationCount;
using ANGI.Application.UseCases.Notification.MarkNotificationAsRead;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Notification;

/// <summary>Registers Use Cases for the Notification module.</summary>
public static class NotificationModule
{
    public static IServiceCollection AddNotificationUseCases(this IServiceCollection services)
    {
        services.AddScoped<IGetNotificationsUseCase, GetNotificationsUseCase>();
        services.AddScoped<IGetUnreadNotificationCountUseCase, GetUnreadNotificationCountUseCase>();
        services.AddScoped<IMarkNotificationAsReadUseCase, MarkNotificationAsReadUseCase>();
        return services;
    }
}
