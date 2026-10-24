using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Social;
using ANGI.Infrastructure.Persistences.Repositories.Social;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Social;

/// <summary>Registers the persistence of the Social module (blogs).</summary>
public static class SocialInfrastructureModule
{
    /// <summary>Registers blog persistence and content-security services.</summary>
    public static IServiceCollection AddSocialInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IBlogRepository, BlogRepository>();
        services.AddScoped<IBlogCommentRepository, BlogCommentRepository>();
        services.AddScoped<IReportRepository, ReportRepository>();
        services.AddScoped<IBlogContentSanitizer, BlogContentSanitizer>();
        return services;
    }
}
