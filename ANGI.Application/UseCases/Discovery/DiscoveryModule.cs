using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.UseCases.Discovery.GetDishDetail;
using ANGI.Application.UseCases.Discovery.SkipPreferenceSurvey;
using ANGI.Application.UseCases.Discovery.SubmitPreferenceSurvey;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Discovery;

/// <summary>Registers Discovery use cases for the lifetime of an HTTP request.</summary>
public static class DiscoveryModule
{
    public static IServiceCollection AddDiscoveryUseCases(this IServiceCollection services)
    {
        services.AddScoped<ISubmitPreferenceSurveyUseCase, SubmitPreferenceSurveyUseCase>();
        services.AddScoped<ISkipPreferenceSurveyUseCase, SkipPreferenceSurveyUseCase>();
        services.AddScoped<IGetDishDetailUseCase, GetDishDetailUseCase>();
        return services;
    }
}
