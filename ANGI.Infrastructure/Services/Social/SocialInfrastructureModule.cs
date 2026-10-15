using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Infrastructure.Persistences.Repositories.Social;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Social;

/// <summary>Registers the persistence of the Social module (blogs).</summary>
public static class SocialInfrastructureModule
{
    public static IServiceCollection AddSocialInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IBlogRepository, BlogRepository>();
        return services;
    }
}
