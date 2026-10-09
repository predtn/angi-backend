using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.UseCases.Auth.EnsureActiveAccount;
using ANGI.Application.UseCases.Auth.Login;
using ANGI.Application.UseCases.Auth.Logout;
using ANGI.Application.UseCases.Auth.Refresh;
using ANGI.Application.UseCases.Auth.Register;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Auth
{
    /// <summary>Centralizes Application-layer use-case registration for the Auth module.</summary>
    public static class AuthModule
    {
        /// <summary>
        /// Registers the Register, Login, Refresh, Logout and account-status use cases per HTTP request.
        /// </summary>
        public static IServiceCollection AddAuthUseCases(this IServiceCollection services)
        {
            services.AddScoped<IRegisterAccountUseCase, RegisterAccountUseCase>();
            services.AddScoped<ILoginUseCase, LoginUseCase>();
            services.AddScoped<IRefreshTokenUseCase, RefreshTokenUseCase>();
            services.AddScoped<ILogoutUseCase, LogoutUseCase>();
            services.AddScoped<IEnsureActiveAccountUseCase, EnsureActiveAccountUseCase>();
            return services;
        }
    }
}
