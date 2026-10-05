using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Infrastructure.Persistences;
using ANGI.Infrastructure.Persistences.Repositories;
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

            return services;
        }
    }
}
