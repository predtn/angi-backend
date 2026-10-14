using ANGI.Application.Common.Interfaces.Repositories.Account;
using ANGI.Infrastructure.Persistences.Repositories.Account;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Account;

/// <summary>Centralizes Infrastructure-layer registration for the Account module.</summary>
public static class AccountInfrastructureModule
{
    /// <summary>Registers account persistence implementations.</summary>
    public static IServiceCollection AddAccountInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IAccountRepository, AccountRepository>();
        return services;
    }
}
