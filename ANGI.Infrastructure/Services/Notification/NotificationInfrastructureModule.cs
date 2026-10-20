using ANGI.Application.Common.Interfaces.Repositories.Notification;
using ANGI.Infrastructure.Persistences.Repositories.Notification;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Notification;

/// <summary>Centralizes Infrastructure-layer registration for the Notification module.</summary>
public static class NotificationInfrastructureModule
{
    /// <summary>Registers notification persistence implementations.</summary>
    public static IServiceCollection AddNotificationInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<INotificationRepository, NotificationRepository>();
        return services;
    }
}
