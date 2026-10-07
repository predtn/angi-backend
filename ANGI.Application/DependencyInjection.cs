using FluentValidation;
using ANGI.Application.UseCases.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddApplication(this IServiceCollection services)
        {
            services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
            services.AddSingleton(TimeProvider.System);
            services.AddAuthUseCases();

            return services;
        }
    }
}
