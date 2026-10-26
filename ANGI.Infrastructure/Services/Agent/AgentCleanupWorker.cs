using ANGI.Application.Common.Interfaces.UseCases.Agent;
using ANGI.Application.Common.Models.Agent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ANGI.Infrastructure.Services.Agent;

/// <summary>Runs bounded cleanup through the Application use case, never directly mutating business rows.</summary>
public sealed class AgentCleanupWorker(IServiceScopeFactory scopes, IConfiguration configuration, AgentLimits limits,
    ILogger<AgentCleanupWorker> logger) : BackgroundService
{
    /// <summary>Periodically expires or deletes eligible memory while keeping the worker alive after transient failures.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Default"))) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(limits.CleanupSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ICleanupAgentConversationsUseCase>().ExecuteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogError(error, "Agent memory cleanup failed."); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
