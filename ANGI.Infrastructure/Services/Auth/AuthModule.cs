using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Infrastructure.Persistences.Repositories.Auth;
using ANGI.Infrastructure.Services.Recommendation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Auth
{
    /// <summary>Centralizes implementation and configuration registration used by the Auth module.</summary>
    public static class AuthModule
    {
        /// <summary>
        /// Maps Auth interfaces to implementations and configures JWT options and the Recommendation HttpClient.
        /// </summary>
        public static IServiceCollection AddAuthInfrastructure(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.AddScoped<IAuthenticationRepository, AuthenticationRepository>();
            services.AddScoped<IPasswordService, PasswordService>();
            services.AddScoped<IAuthenticationTokenService, AuthenticationTokenService>();
            services.AddScoped<ICurrentUserService, CurrentUserService>();
            services.AddHttpContextAccessor();

            services.AddOptions<JwtTokenSettings>()
                .Bind(configuration.GetSection(JwtTokenSettings.SectionName));

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
