using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Infrastructure.Persistences.Repositories.Discovery;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Discovery;

/// <summary>Registers the persistence of the Discovery module.</summary>
public static class DiscoveryInfrastructureModule
{
    public static IServiceCollection AddDiscoveryInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IDishRepository, DishRepository>();
        return services;
    }
}
