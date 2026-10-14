using ANGI.Application.Common.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Mapbox
{
    /// <summary>Registers the shared Mapbox Directions client and its in-memory route cache.</summary>
    public static class MapboxModule
    {
        /// <summary>Binds Mapbox settings and configures the typed route client.</summary>
        public static IServiceCollection AddMapboxInfrastructure(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            var mapboxSection = configuration.GetSection(MapboxSettings.SectionName);
            var mapboxSettings = mapboxSection.Get<MapboxSettings>() ?? new MapboxSettings();

            services.AddOptions<MapboxSettings>().Bind(mapboxSection);
            services.AddMemoryCache();
            services.AddHttpClient<IRouteService, MapboxRouteService>(client =>
            {
                if (Uri.TryCreate(mapboxSettings.BaseUrl, UriKind.Absolute, out var baseAddress))
                {
                    client.BaseAddress = baseAddress;
                }

                client.Timeout = TimeSpan.FromMilliseconds(
                    mapboxSettings.TimeoutMilliseconds > 0
                        ? mapboxSettings.TimeoutMilliseconds
                        : 3000);
            });

            return services;
        }
    }
}
