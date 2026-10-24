using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.UseCases.Administration.GetModPermissions;
using ANGI.Application.UseCases.Administration.GetModDetail;
using ANGI.Application.UseCases.Administration.GetPermissionCatalog;
using ANGI.Application.UseCases.Administration.CreateMod;
using ANGI.Application.UseCases.Administration.ListMods;
using ANGI.Application.UseCases.Administration.ReplaceModPermissions;
using ANGI.Application.UseCases.Administration.ReplaceModRolePermissions;
using ANGI.Application.UseCases.Administration.UpdateMod;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Administration;

/// <summary>Centralizes Application-layer registration for the Administration module.</summary>
public static class AdministrationModule
{
    /// <summary>Registers account and permission administration use cases for the lifetime of an HTTP request.</summary>
    public static IServiceCollection AddAdministrationUseCases(this IServiceCollection services)
    {
        services.AddScoped<IListModsUseCase, ListModsUseCase>();
        services.AddScoped<ICreateModUseCase, CreateModUseCase>();
        services.AddScoped<IGetModDetailUseCase, GetModDetailUseCase>();
        services.AddScoped<IUpdateModUseCase, UpdateModUseCase>();
        services.AddScoped<IGetPermissionCatalogUseCase, GetPermissionCatalogUseCase>();
        services.AddScoped<IGetModPermissionsUseCase, GetModPermissionsUseCase>();
        services.AddScoped<IReplaceModPermissionsUseCase, ReplaceModPermissionsUseCase>();
        services.AddScoped<IReplaceModRolePermissionsUseCase, ReplaceModRolePermissionsUseCase>();
        return services;
    }
}
