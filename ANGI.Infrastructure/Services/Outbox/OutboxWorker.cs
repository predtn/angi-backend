using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ANGI.Infrastructure.Services.Outbox;

/// <summary>
/// Sends core.outbox_messages to reco (project_architecture.md, Outbox). It polls every PollSeconds, at once again after a
/// full batch. Without Recommendation:BaseUrl and ApiKey it does nothing and the rows stay pending.
/// </summary>
public sealed class OutboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxSettings _settings;
    private readonly ILogger<OutboxWorker> _logger;

    public OutboxWorker(IServiceScopeFactory scopeFactory, IOptions<OutboxSettings> settings, ILogger<OutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            if (!scope.ServiceProvider.GetRequiredService<IOutboxSender>().IsConfigured)
            {
                _logger.LogWarning("Recommendation:BaseUrl or ApiKey is not set; the outbox worker does not run.");
                return;
            }
        }

        var pollDelay = TimeSpan.FromSeconds(Math.Max(1, _settings.PollSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                claimed = await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Database down or similar: the claimed rows' locks expire and are picked up again later.
                _logger.LogError(exception, "Outbox dispatch failed; retrying after the poll delay.");
            }

            if (claimed < Math.Max(1, _settings.BatchSize))
            {
                try
                {
                    await Task.Delay(pollDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
