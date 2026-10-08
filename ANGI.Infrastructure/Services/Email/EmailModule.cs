using ANGI.Application.Common.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Email
{
    /// <summary>Registers the email client, used by several modules (Auth, Administration).</summary>
    public static class EmailModule
    {
        /// <summary>Binds the Brevo and Frontend settings and configures the typed HttpClient.</summary>
        public static IServiceCollection AddEmailInfrastructure(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            var brevoSection = configuration.GetSection(BrevoSettings.SectionName);
            var brevoSettings = brevoSection.Get<BrevoSettings>() ?? new BrevoSettings();
            services.AddOptions<BrevoSettings>().Bind(brevoSection);
            services.AddOptions<FrontendSettings>()
                .Bind(configuration.GetSection(FrontendSettings.SectionName));
            services.AddHttpClient<IEmailService, EmailService>(client =>
            {
                // Without the trailing slash "smtp/email" would replace the last segment ("v3").
                var baseUrl = brevoSettings.BaseUrl.EndsWith('/') ? brevoSettings.BaseUrl : brevoSettings.BaseUrl + "/";
                if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseAddress))
                {
                    client.BaseAddress = baseAddress;
                }

                client.Timeout = TimeSpan.FromMilliseconds(
                    brevoSettings.TimeoutMilliseconds > 0
                        ? brevoSettings.TimeoutMilliseconds
                        : 10000);
            });

            return services;
        }
    }
}
