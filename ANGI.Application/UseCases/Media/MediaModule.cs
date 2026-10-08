using ANGI.Application.Common.Interfaces.UseCases.Media;
using ANGI.Application.UseCases.Media.Upload;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Media;

/// <summary>Centralizes Application-layer registration for the Media module.</summary>
public static class MediaModule
{
    /// <summary>Registers media use cases for the lifetime of an HTTP request.</summary>
    public static IServiceCollection AddMediaUseCases(this IServiceCollection services)
    {
        services.AddScoped<IUploadMediaUseCase, UploadMediaUseCase>();
        return services;
    }
}
