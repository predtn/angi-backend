using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Infrastructure.Persistences;
using ANGI.Infrastructure.Persistences.Repositories;
using ANGI.Infrastructure.Services;
using ANGI.Infrastructure.Settings;
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
            services.Configure<BrevoSettings>(configuration.GetSection(BrevoSettings.SectionName));

            services.AddDbContext<ANGIContext>(options =>
                options.UseNpgsql(configuration.GetConnectionString("Default"),
                                  npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", ANGIContext.Schema))
                       .UseSnakeCaseNamingConvention());

            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddScoped<ICloudinaryService, CloudinaryService>();

            services.AddHttpClient<IEmailService, EmailService>((sp, client) =>
            {
                var settings = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BrevoSettings>>().Value;
                client.BaseAddress = new Uri("https://api.brevo.com/v3/");
                client.DefaultRequestHeaders.Add("api-key", settings.ApiKey);
                client.DefaultRequestHeaders.Add("accept", "application/json");
            });

            return services;
        }
    }
}
