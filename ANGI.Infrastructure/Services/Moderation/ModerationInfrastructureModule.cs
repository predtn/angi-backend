using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Infrastructure.Persistences.Repositories.Moderation;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Moderation;

/// <summary>Centralizes Infrastructure-layer registrations for moderation features.</summary>
public static class ModerationInfrastructureModule
{
    /// <summary>Registers moderation persistence implementations.</summary>
    public static IServiceCollection AddModerationInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IVerificationReviewRepository, VerificationReviewRepository>();
        return services;
    }
}
