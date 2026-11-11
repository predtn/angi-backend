namespace ANGI.WebApi.Configs
{
    public static class CorsConfig
    {
        public const string PolicyName = "Frontend";

        public static IServiceCollection AddCorsConfiguration(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            // STEP-01: Prepare allowedOrigins from configuration.GetSection.
            var allowedOrigins = configuration
                .GetSection("Cors:AllowedOrigins")
                .Get<string[]>() ?? [];

            // STEP-02: Register AddCors.
            services.AddCors(options =>
            {
                options.AddPolicy(PolicyName, policy =>
                {
                    if (allowedOrigins.Length > 0)
                    {
                        policy.WithOrigins(allowedOrigins)
                            .AllowAnyHeader()
                            .AllowAnyMethod()
                            .WithExposedHeaders("Retry-After");
                    }
                });
            });

            // STEP-03: Return services.
            return services;
        }
    }
}
