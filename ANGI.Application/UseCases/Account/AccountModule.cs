using ANGI.Application.Common.Interfaces.UseCases.Account;
using ANGI.Application.UseCases.Account.GetMyProfile;
using ANGI.Application.UseCases.Account.UpdateMyProfile;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Account;

/// <summary>Registers Account use cases for the lifetime of an HTTP request.</summary>
public static class AccountModule
{
    public static IServiceCollection AddAccountUseCases(this IServiceCollection services)
    {
        services.AddScoped<IGetMyProfileUseCase, GetMyProfileUseCase>();
        services.AddScoped<IUpdateMyProfileUseCase, UpdateMyProfileUseCase>();
        return services;
    }
}
