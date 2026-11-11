using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Services.Discovery;
using ANGI.Infrastructure.Persistences.Repositories.Discovery;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Discovery;

/// <summary>Registers the persistence and services of the Discovery module.</summary>
public static class DiscoveryInfrastructureModule
{
    public static IServiceCollection AddDiscoveryInfrastructure(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddScoped<IDishRepository, DishRepository>();
        services.AddScoped<ITagRepository, TagRepository>();
        services.AddSingleton<IRankedDishListCache, MemoryRankedDishListCache>();
        return services;
    }
}
