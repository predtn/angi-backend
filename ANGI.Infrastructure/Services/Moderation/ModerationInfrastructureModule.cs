using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Infrastructure.Persistences.Repositories.Moderation;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Moderation;

/// <summary>Centralizes Infrastructure-layer registrations for moderation features.</summary>
public static class ModerationInfrastructureModule
{
    /// <summary>Registers moderation persistence and the account suspension expiry worker.</summary>
    public static IServiceCollection AddModerationInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IVerificationReviewRepository, VerificationReviewRepository>();
        services.AddScoped<IMenuReviewRepository, MenuReviewRepository>();
        services.AddScoped<IRestaurantModerationRepository, RestaurantModerationRepository>();
        services.AddScoped<IUserModerationRepository, UserModerationRepository>();
        services.AddHostedService<AccountSuspensionExpiryWorker>();
        return services;
    }
}
