using Microsoft.AspNetCore.HttpOverrides;

namespace ANGI.WebApi.Configs
{
    public static class ForwardedHeadersConfig
    {
        /// <summary>
        /// Lets RemoteIpAddress be the client behind the hosting proxy, so the per-IP rate limits (anonymous requests,
        /// login failures per email + IP) count each client instead of the proxy.
        /// </summary>
        public static IServiceCollection AddForwardedHeadersConfiguration(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            var section = configuration.GetSection(ForwardedHeadersSettings.SectionName);
            services.AddOptions<ForwardedHeadersSettings>()
                .Bind(section)
                .Validate(settings => settings.ForwardLimit > 0, "ForwardedHeaders:ForwardLimit must be greater than zero.")
                .ValidateOnStart();

            var settings = section.Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = settings.ForwardLimit;

                // Render's proxy addresses are not published; the app is reachable only through it, so trust any proxy.
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            });

            return services;
        }

        /// <summary>Applies the forwarded client IP and scheme first, before anything reads RemoteIpAddress.</summary>
        public static IApplicationBuilder UseForwardedHeadersIfEnabled(this WebApplication app)
        {
            var settings = app.Configuration.GetSection(ForwardedHeadersSettings.SectionName).Get<ForwardedHeadersSettings>();
            return settings?.Enabled == true ? app.UseForwardedHeaders() : app;
        }
    }
}
