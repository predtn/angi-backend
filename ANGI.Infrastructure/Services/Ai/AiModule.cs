using ANGI.Application.Common.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Ai;

/// <summary>Registers the AI provider used by roadmap generation (RM-10).</summary>
public static class AiModule
{
    /// <summary>Binds AI settings and configures the typed OpenAI client.</summary>
    public static IServiceCollection AddAiInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var aiSection = configuration.GetSection(AiSettings.SectionName);
        var aiSettings = aiSection.Get<AiSettings>() ?? new AiSettings();

        services.AddOptions<AiSettings>().Bind(aiSection);
        services.AddHttpClient<IRoadmapPlannerService, OpenAiRoadmapPlannerService>(client =>
        {
            if (Uri.TryCreate(aiSettings.BaseUrl, UriKind.Absolute, out var baseAddress))
            {
                client.BaseAddress = baseAddress;
            }

            client.Timeout = TimeSpan.FromSeconds(aiSettings.TimeoutSeconds > 0 ? aiSettings.TimeoutSeconds : 30);
        });
        return services;
    }
}
