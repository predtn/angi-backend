using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Infrastructure.Persistences.Repositories.Moderation;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Moderation;

/// <summary>Registers persistence for menu moderation.</summary>
public static class ModerationInfrastructureModule
{
    /// <summary>Adds the scoped menu review repository to the infrastructure container.</summary>
    public static IServiceCollection AddModerationInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IMenuReviewRepository, MenuReviewRepository>();
        return services;
    }
}
