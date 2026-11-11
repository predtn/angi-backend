using ANGI.Infrastructure.Services.Recommendation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Outbox;

/// <summary>Registers the outbox worker that sends dish changes and likes / dislikes to reco.</summary>
public static class OutboxModule
{
    public static IServiceCollection AddOutboxInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var outboxSection = configuration.GetSection(OutboxSettings.SectionName);
        var outboxSettings = outboxSection.Get<OutboxSettings>() ?? new OutboxSettings();
        var recommendationSettings = configuration.GetSection(RecommendationSettings.SectionName).Get<RecommendationSettings>()
            ?? new RecommendationSettings();
        services.AddOptions<OutboxSettings>().Bind(outboxSection);

        services.AddHttpClient<IOutboxSender, RecoOutboxSender>(client =>
        {
            if (Uri.TryCreate(recommendationSettings.BaseUrl, UriKind.Absolute, out var baseAddress))
            {
                client.BaseAddress = baseAddress;
            }

            client.Timeout = TimeSpan.FromSeconds(outboxSettings.TimeoutSeconds > 0 ? outboxSettings.TimeoutSeconds : 10);
        });
        services.AddScoped<OutboxStore>();
        services.AddScoped<OutboxDispatcher>();
        services.AddHostedService<OutboxWorker>();
        return services;
    }
}
