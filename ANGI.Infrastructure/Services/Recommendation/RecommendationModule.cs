using ANGI.Application.Common.Interfaces.Services.Recommendation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Recommendation
{
    /// <summary>Registers the client of the recommendation service, used by several modules.</summary>
    public static class RecommendationModule
    {
        /// <summary>Binds the Recommendation settings and configures the typed HttpClient.</summary>
        public static IServiceCollection AddRecommendationInfrastructure(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            var recommendationSection = configuration.GetSection(RecommendationSettings.SectionName);
            var recommendationSettings = recommendationSection.Get<RecommendationSettings>()
                ?? new RecommendationSettings();
            services.AddOptions<RecommendationSettings>().Bind(recommendationSection);
            services.AddHttpClient<IRecommendationService, RecommendationService>(client =>
            {
                if (Uri.TryCreate(recommendationSettings.BaseUrl, UriKind.Absolute, out var baseAddress))
                {
                    client.BaseAddress = baseAddress;
                }

                client.Timeout = TimeSpan.FromMilliseconds(
                    recommendationSettings.TimeoutMilliseconds > 0
                        ? recommendationSettings.TimeoutMilliseconds
                        : 800);
            });

            return services;
        }
    }
}
