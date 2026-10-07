using ANGI.Application.Common.Interfaces.Repositories.Auth;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Infrastructure.Persistences.Repositories.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Auth
{
    /// <summary>Centralizes implementation and configuration registration used by the Auth module.</summary>
    public static class AuthModule
    {
        /// <summary>
        /// Maps Auth interfaces to implementations and configures JWT options.
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

            return services;
        }
    }
}
