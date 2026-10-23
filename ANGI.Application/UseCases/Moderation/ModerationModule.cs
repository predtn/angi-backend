using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.UseCases.Moderation.GetModRestaurant;
using ANGI.Application.UseCases.Moderation.ExpireUserSuspensions;
using ANGI.Application.UseCases.Moderation.ListModRestaurants;
using ANGI.Application.UseCases.Moderation.ResolveReport;
using ANGI.Application.UseCases.Moderation.UpdateDishModerationStatus;
using ANGI.Application.UseCases.Moderation.UpdateRestaurantModerationStatus;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Moderation;

/// <summary>Registers moderation authorization, shared effects and the ANGI-191 use cases.</summary>
public static class ModerationModule
{
    /// <summary>Adds scoped moderation operations to the application container.</summary>
    public static IServiceCollection AddModerationUseCases(this IServiceCollection services)
    {
        services.AddScoped<ModerationAccess>();
        services.AddScoped<RestaurantModerationEffects>();
        services.AddScoped<IListModRestaurantsUseCase, ListModRestaurantsUseCase>();
        services.AddScoped<IGetModRestaurantUseCase, GetModRestaurantUseCase>();
        services.AddScoped<IUpdateRestaurantModerationStatusUseCase, UpdateRestaurantModerationStatusUseCase>();
        services.AddScoped<IUpdateDishModerationStatusUseCase, UpdateDishModerationStatusUseCase>();
        services.AddScoped<IResolveReportUseCase, ResolveReportUseCase>();
        services.AddScoped<IExpireUserSuspensionsUseCase, ExpireUserSuspensionsUseCase>();
        return services;
    }
}
