using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Infrastructure.Persistences.Repositories.Administration;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Administration;

/// <summary>Centralizes Infrastructure-layer registration for the Administration module.</summary>
public static class AdministrationInfrastructureModule
{
    /// <summary>Registers permission administration persistence for the lifetime of an HTTP request.</summary>
    public static IServiceCollection AddAdministrationInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IAdministrationPermissionRepository, AdministrationPermissionRepository>();
        return services;
    }
}
