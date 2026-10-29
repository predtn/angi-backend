using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Infrastructure.Persistences.Repositories.Agent;
using ANGI.Infrastructure.Services.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Services.Agent;

/// <summary>Registers Agent persistence, bounded provider transport, telemetry and retention scheduling.</summary>
public static class AgentModule
{
    /// <summary>Binds validated backend limits and reuses the existing AI provider settings.</summary>
    public static IServiceCollection AddAgentInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var limits = configuration.GetSection(AgentLimits.SectionName).Get<AgentLimits>() ?? new();
        if (!Valid(limits)) throw new InvalidOperationException("Invalid Agent resource or retention limits.");
        services.AddSingleton(limits);
        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddScoped<IAgentTelemetry, AgentTelemetry>();
        var ai = configuration.GetSection(AiSettings.SectionName).Get<AiSettings>() ?? new();
        // Nối IAgentLlmService mà Orchestrator sử dụng với adapter HTTP OpenAiAgentLlmService.
        // BaseUrl lấy từ Ai; timeout riêng từ Agent, không thay đổi timeout RM-10.
        // API key lấy qua IOptions<AiSettings> (AiModule đăng ký)
        // và chỉ gắn header trong OpenAiAgentLlmService.RunAsync khi gửi request.
        services.AddHttpClient<IAgentLlmService, OpenAiAgentLlmService>(client =>
        {
            if (Uri.TryCreate(ai.BaseUrl, UriKind.Absolute, out var address)) client.BaseAddress = address;
            client.Timeout = TimeSpan.FromSeconds(Math.Min(limits.LlmTimeoutSeconds, limits.TimeoutSeconds));
            client.MaxResponseContentBufferSize = 2_000_000;
        });
        services.AddHostedService<AgentCleanupWorker>();
        return services;
    }

    /// <summary>Rejects configurations that bypass schema caps, cost guards, lease fencing or retention rules.</summary>
    private static bool Valid(AgentLimits limits) => limits.MaxToolRounds is >= 1 and <= 10 && limits.MaxToolCalls is >= 1 and <= 20 &&
        limits.MaxLlmCalls >= 2 && limits.MaxLlmCalls <= 20 && limits.MaxMessageLength is >= 1 and <= 4000 &&
        limits.MaxContextCharacters is >= 4000 and <= 500000 && limits.MaxHistoryMessages is >= 1 and <= 100 &&
        limits.MaxCandidates is >= 1 and <= 2000 && limits.MaxRanked is >= 1 and <= 200 &&
        limits.LlmTimeoutSeconds is >= 1 and <= 300 && limits.TimeoutSeconds is >= 1 and <= 300 &&
        limits.LeaseSeconds > limits.TimeoutSeconds && limits.LeaseSeconds <= 600 &&
        limits.MessagesPerMinute > 0 && limits.MessagesPerDay >= limits.MessagesPerMinute && limits.ActiveDays > 0 &&
        limits.RetentionDays >= limits.ActiveDays && limits.RetentionDays <= 365 && limits.CleanupSeconds is >= 1 and <= 86400 &&
        limits.MaxOutputTokens is >= 100 and <= 16000;
}
