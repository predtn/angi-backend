using ANGI.Application.Common.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ANGI.Infrastructure.Services.Ai;

/// <summary>Registers the AI provider used by roadmap generation (RM-10).</summary>
public static class AiModule
{
    /// <summary>Binds AI settings and configures the typed OpenAI client.</summary>
    public static IServiceCollection AddAiInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var aiSection = configuration.GetSection(AiSettings.SectionName);
        var aiSettings = aiSection.Get<AiSettings>() ?? new AiSettings();

        // Bind section "Ai" vào IOptions<AiSettings>, dùng chung cho RM-10 và OpenAiAgentLlmService.
        // Agent đọc Ai:ApiKey (biến môi trường Ai__ApiKey) từ options và gắn vào Authorization header.
        services.AddOptions<AiSettings>().Bind(aiSection);
        services.TryAddTransient<AiBudgetGatewayHandler>();
        services.TryAddSingleton<IConfiguration>(configuration);
        services.AddHttpClient<IRoadmapPlannerService, OpenAiRoadmapPlannerService>(client =>
        {
            if (Uri.TryCreate(aiSettings.BaseUrl, UriKind.Absolute, out var baseAddress))
            {
                client.BaseAddress = baseAddress;
            }

            client.Timeout = TimeSpan.FromSeconds(aiSettings.TimeoutSeconds > 0 ? aiSettings.TimeoutSeconds : 30);
        }).AddHttpMessageHandler<AiBudgetGatewayHandler>()
          .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }
}
