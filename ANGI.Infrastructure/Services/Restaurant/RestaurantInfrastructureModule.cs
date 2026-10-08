using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Infrastructure.Persistences.Repositories.Restaurant;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Restaurant;

/// <summary>Centralizes Infrastructure-layer registration for the Restaurant module.</summary>
public static class RestaurantInfrastructureModule
{
    /// <summary>Registers restaurant persistence implementations.</summary>
    public static IServiceCollection AddRestaurantInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IRestaurantRepository, RestaurantRepository>();
        return services;
    }
}
