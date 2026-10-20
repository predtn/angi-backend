using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.UseCases.Roadmap.Create;
using ANGI.Application.UseCases.Roadmap.GetMyRoadmaps;
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
        services.AddScoped<IGetMyRoadmapsUseCase, GetMyRoadmapsUseCase>();
        services.AddScoped<IUpdateRoadmapUseCase, UpdateRoadmapUseCase>();
        return services;
    }
}
