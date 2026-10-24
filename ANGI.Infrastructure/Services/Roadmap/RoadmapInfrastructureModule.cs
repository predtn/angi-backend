using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Infrastructure.Persistences.Repositories.Roadmap;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Roadmap;

/// <summary>Centralizes Infrastructure-layer registration for the Roadmap module.</summary>
public static class RoadmapInfrastructureModule
{
    /// <summary>Registers roadmap persistence and the RM-10 generation worker.</summary>
    public static IServiceCollection AddRoadmapInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IRoadmapRepository, RoadmapRepository>();
        services.AddScoped<IRoadmapGenerationRepository, RoadmapGenerationRepository>();
        services.AddOptions<RoadmapGenerationSettings>().Bind(configuration.GetSection(RoadmapGenerationSettings.SectionName));
        services.AddHostedService<RoadmapGenerationWorker>();
        return services;
    }
}
