using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.UseCases.Roadmap.Create;
using ANGI.Application.UseCases.Roadmap.Delete;
using ANGI.Application.UseCases.Roadmap.GetMyRoadmaps;
using ANGI.Application.UseCases.Roadmap.GetRoadmapDayRoute;
using ANGI.Application.UseCases.Roadmap.GetRoadmapDetail;
using ANGI.Application.UseCases.Roadmap.Update;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Roadmap;

/// <summary>Centralizes Application-layer registration for the Roadmap module.</summary>
public static class RoadmapModule
{
    /// <summary>Registers roadmap planning use cases for the lifetime of an HTTP request.</summary>
    public static IServiceCollection AddRoadmapUseCases(this IServiceCollection services)
    {
        services.AddScoped<ICreateRoadmapUseCase, CreateRoadmapUseCase>();
        services.AddScoped<IDeleteRoadmapUseCase, DeleteRoadmapUseCase>();
        services.AddScoped<IGetMyRoadmapsUseCase, GetMyRoadmapsUseCase>();
        services.AddScoped<IGetRoadmapDetailUseCase, GetRoadmapDetailUseCase>();
        services.AddScoped<IGetRoadmapDayRouteUseCase, GetRoadmapDayRouteUseCase>();
        services.AddScoped<IUpdateRoadmapUseCase, UpdateRoadmapUseCase>();
        return services;
    }
}
