namespace ANGI.WebApi.Configs
{
    public static class CorsConfig
    {
        public const string PolicyName = "Frontend";

        public static IServiceCollection AddCorsConfiguration(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            var allowedOrigins = configuration
                .GetSection("Cors:AllowedOrigins")
                .Get<string[]>() ?? [];

            services.AddCors(options =>
            {
                options.AddPolicy(PolicyName, policy =>
                {
                    if (allowedOrigins.Length > 0)
                    {
                        policy.WithOrigins(allowedOrigins)
                            .AllowAnyHeader()
                            .AllowAnyMethod();
                    }
                });
            });

            return services;
        }
    }
}
