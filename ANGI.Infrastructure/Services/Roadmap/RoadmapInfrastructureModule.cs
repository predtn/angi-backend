using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Infrastructure.Persistences.Repositories.Roadmap;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Roadmap;

/// <summary>Centralizes Infrastructure-layer registration for the Roadmap module.</summary>
public static class RoadmapInfrastructureModule
{
    /// <summary>Registers roadmap persistence implementations.</summary>
    public static IServiceCollection AddRoadmapInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IRoadmapRepository, RoadmapRepository>();
        return services;
    }
}
