using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ANGI.Infrastructure.Services.Roadmap;

/// <summary>
/// Runs queued RM-10 jobs one at a time (project_architecture.md, AI roadmap generation). On start it puts jobs left
/// running by a stopped host back in the queue; then it claims the oldest queued job, at once again after one, or after
/// PollSeconds when none waits. Without ConnectionStrings:Default (HTTP contract test hosts) it does nothing.
/// </summary>
public sealed class RoadmapGenerationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly RoadmapGenerationSettings _settings;
    private readonly ILogger<RoadmapGenerationWorker> _logger;

    public RoadmapGenerationWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IOptions<RoadmapGenerationSettings> settings,
        ILogger<RoadmapGenerationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_configuration.GetConnectionString("Default")))
        {
            _logger.LogWarning("ConnectionStrings:Default is not set; the roadmap generation worker does not run.");
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRoadmapGenerationRepository>().RequeueInterruptedAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not requeue interrupted roadmap generation jobs.");
        }

        var pollDelay = TimeSpan.FromSeconds(Math.Max(1, _settings.PollSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            long? jobId = null;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                jobId = await scope.ServiceProvider.GetRequiredService<IRoadmapGenerationRepository>().ClaimNextJobAsync(stoppingToken);
                if (jobId is { } claimed)
                {
                    await scope.ServiceProvider.GetRequiredService<IProcessRoadmapGenerationJobUseCase>()
                        .ExecuteAsync(claimed, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // A job cut by shutdown stays running and is requeued at the next start.
                break;
            }
            catch (Exception exception)
            {
                // The use case has already marked the job failed; a database outage just waits for the next poll.
                _logger.LogError(exception, "Roadmap generation job {JobId} failed.", jobId);
            }

            if (jobId is null)
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
