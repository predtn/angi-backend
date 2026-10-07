using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Infrastructure.Persistences;
using ANGI.Infrastructure.Persistences.Repositories;
using ANGI.Infrastructure.Services;
using ANGI.Infrastructure.Services.Auth;
using ANGI.Infrastructure.Services.Recommendation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddInfrastructure(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.AddDbContext<ANGIContext>(options =>
                options.UseNpgsql(configuration.GetConnectionString("Default"),
                                  npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", ANGIContext.Schema))
                       .UseSnakeCaseNamingConvention());
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddScoped<ICloudinaryService, CloudinaryService>();
            services.AddAuthInfrastructure(configuration);
            services.AddRecommendationInfrastructure(configuration);

            return services;
        }
    }
}
