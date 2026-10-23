using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Infrastructure.Persistences.Repositories.Moderation;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Moderation;

/// <summary>Registers persistence required by the moderation module.</summary>
public static class ModerationModule
{
    /// <summary>Adds a scoped moderation repository sharing the request's unit of work.</summary>
    public static IServiceCollection AddModerationInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IRestaurantModerationRepository, RestaurantModerationRepository>();
        services.AddHostedService<AccountSuspensionExpiryWorker>();
        return services;
    }
}
