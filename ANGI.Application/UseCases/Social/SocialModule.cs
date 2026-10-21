using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.UseCases.Social.Create;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Social;

/// <summary>Registers use cases of the Social module.</summary>
public static class SocialModule
{
    public static IServiceCollection AddSocialUseCases(this IServiceCollection services)
    {
        services.AddScoped<ICreateBlogUseCase, CreateBlogUseCase>();
        return services;
    }
}
