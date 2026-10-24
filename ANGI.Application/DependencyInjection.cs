using FluentValidation;
using ANGI.Application.UseCases.Account;
using ANGI.Application.UseCases.Auth;
using ANGI.Application.UseCases.Discovery;
using ANGI.Application.UseCases.Media;
using ANGI.Application.UseCases.Notification;
using ANGI.Application.UseCases.Restaurant;
using ANGI.Application.UseCases.Roadmap;
using ANGI.Application.UseCases.Social;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application
{
    /// <summary>Provides dependency registration for the Application layer.</summary>
    public static class DependencyInjection
    {
        /// <summary>Registers validators, shared utilities, and feature use cases.</summary>
        public static IServiceCollection AddApplication(this IServiceCollection services)
        {
            services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
            services.AddSingleton(TimeProvider.System);
            // Thread-safe shared generator, injected so tests can seed it (DISH-03 weighted roll).
            services.AddSingleton(Random.Shared);
            services.AddAccountUseCases();
            services.AddAuthUseCases();
            services.AddDiscoveryUseCases();
            services.AddMediaUseCases();
            services.AddNotificationUseCases();
            services.AddRestaurantUseCases();
            services.AddRoadmapUseCases();
            services.AddSocialUseCases();

            return services;
        }
    }
}
