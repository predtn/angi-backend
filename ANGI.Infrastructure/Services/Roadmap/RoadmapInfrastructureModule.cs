using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Infrastructure.Persistences.Repositories.Roadmap;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Roadmap;

/// <summary>Registers persistence and services for the Roadmap module.</summary>
public static class RoadmapInfrastructureModule
{
    public static IServiceCollection AddRoadmapInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IRoadmapRepository, RoadmapRepository>();
        return services;
    }
}
