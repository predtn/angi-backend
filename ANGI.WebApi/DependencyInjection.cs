using ANGI.WebApi.Middlewares;

namespace ANGI.WebApi
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddWebApi(this IServiceCollection services)
        {
            services.AddScoped<ExceptionHandlingMiddleware>();

            return services;
        }
    }
}
