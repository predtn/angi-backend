using ANGI.Application.Common.Interfaces.UseCases.Account;
using ANGI.Application.UseCases.Account.ChangePassword;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Account;

/// <summary>Centralizes Application-layer use-case registration for the Account module.</summary>
public static class AccountModule
{
    /// <summary>Registers account management use cases with a scoped lifetime.</summary>
    public static IServiceCollection AddAccountUseCases(this IServiceCollection services)
    {
        services.AddScoped<IChangePasswordUseCase, ChangePasswordUseCase>();
        return services;
    }
}
