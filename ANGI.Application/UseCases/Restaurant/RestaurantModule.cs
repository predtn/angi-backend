using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.UseCases.Restaurant.Register;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Restaurant;

/// <summary>Centralizes Application-layer registration for the Restaurant module.</summary>
public static class RestaurantModule
{
    /// <summary>Registers restaurant use cases for the lifetime of an HTTP request.</summary>
    public static IServiceCollection AddRestaurantUseCases(this IServiceCollection services)
    {
        services.AddScoped<IRegisterRestaurantUseCase, RegisterRestaurantUseCase>();
        return services;
    }
}
