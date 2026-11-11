using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ANGI.Infrastructure.Services.Moderation;

/// <summary>Checks temporary suspension deadlines once per minute and drains expired accounts in bounded batches.</summary>
public sealed class AccountSuspensionExpiryWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<AccountSuspensionExpiryWorker> logger) : BackgroundService
{
    /// <summary>Runs expiry operations in fresh scopes and retries on the next tick after a database failure.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    int expired;
                    do
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        expired = await scope.ServiceProvider.GetRequiredService<IExpireUserSuspensionsUseCase>().ExecuteAsync(stoppingToken);
                    } while (expired > 0 && !stoppingToken.IsCancellationRequested);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogError(exception, "Failed to expire temporary account suspensions."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
