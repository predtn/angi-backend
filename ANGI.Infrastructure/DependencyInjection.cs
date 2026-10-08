using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Infrastructure.Persistences;
using ANGI.Infrastructure.Persistences.Repositories;
using ANGI.Infrastructure.Services.Audit;
using ANGI.Infrastructure.Services.Auth;
using ANGI.Infrastructure.Services.Email;
using ANGI.Infrastructure.Services.Media;
using ANGI.Infrastructure.Services.Recommendation;
using ANGI.Infrastructure.Services.Restaurant;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ANGI.Infrastructure
{
    /// <summary>Provides dependency registration for the Infrastructure layer.</summary>
    public static class DependencyInjection
    {
        /// <summary>Registers the database context and all infrastructure feature modules.</summary>
        public static IServiceCollection AddInfrastructure(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.TryAddSingleton(TimeProvider.System);
            services.AddDbContext<ANGIContext>(options =>
                options.UseNpgsql(configuration.GetConnectionString("Default"),
                                  npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", ANGIContext.Schema))
                       .UseSnakeCaseNamingConvention());
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddAuthInfrastructure(configuration);
            services.AddMediaInfrastructure();
            services.AddRecommendationInfrastructure(configuration);
            services.AddEmailInfrastructure(configuration);
            services.AddRestaurantInfrastructure();
            services.AddAuditInfrastructure();

            return services;
        }
    }
}
