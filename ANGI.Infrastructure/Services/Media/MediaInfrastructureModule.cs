using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Infrastructure.Persistences.Repositories.Media;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Media;

/// <summary>Centralizes Infrastructure-layer registration for the Media module.</summary>
public static class MediaInfrastructureModule
{
    /// <summary>Registers media persistence and Cloudinary storage implementations.</summary>
    public static IServiceCollection AddMediaInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IMediaRepository, MediaRepository>();
        services.AddScoped<ICloudinaryService, CloudinaryService>();
        return services;
    }
}
