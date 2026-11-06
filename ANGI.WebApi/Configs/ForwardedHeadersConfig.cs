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
            // STEP-01: Prepare section through configuration.GetSection.
            var section = configuration.GetSection(ForwardedHeadersSettings.SectionName);
            // STEP-02: Run Bind().Validate(filtered).ValidateOnStart.
            services.AddOptions<ForwardedHeadersSettings>()
                .Bind(section)
                .Validate(settings => settings.ForwardLimit > 0, "ForwardedHeaders:ForwardLimit must be greater than zero.")
                .ValidateOnStart();

            // STEP-03: Prepare settings, then call services.Configure.
            var settings = section.Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = settings.ForwardLimit;

                // Render's proxy addresses are not published; the app is reachable only through it, so trust any proxy.
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            });

            // STEP-04: Return services.
            return services;
        }

        /// <summary>
        /// Binds TrustedClientIp:ProxyKey for TrustedClientIpMiddleware: empty (off) or at least 32 characters, like the
        /// other shared secrets.
        /// </summary>
        public static IServiceCollection AddTrustedClientIpConfiguration(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.AddOptions<TrustedClientIpSettings>()
                .Bind(configuration.GetSection(TrustedClientIpSettings.SectionName))
                .Validate(
                    settings => settings.ProxyKey.Length == 0 || settings.ProxyKey.Length >= 32,
                    "TrustedClientIp:ProxyKey must be empty or at least 32 characters.")
                .ValidateOnStart();
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
