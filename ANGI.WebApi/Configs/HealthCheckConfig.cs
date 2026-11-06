using ANGI.Infrastructure.Persistences;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ANGI.WebApi.Configs
{
    public static class HealthCheckConfig
    {
        public const string Path = "/health";

        /// <summary>Registers the database check that /health runs.</summary>
        public static IServiceCollection AddHealthCheckConfiguration(this IServiceCollection services)
        {
            services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
            return services;
        }

        /// <summary>
        /// OPS-03 GET /health for the host (Render health check path): 200 with data.status = "Healthy" when PostgreSQL
        /// answers, 503 SERVICE_UNAVAILABLE otherwise, both in ApiResponse. Outside /api, so it is neither rate limited nor
        /// authenticated, and it reveals nothing beyond the status.
        /// </summary>
        public static IEndpointRouteBuilder MapHealthCheckEndpoint(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapHealthChecks(Path, new HealthCheckOptions
            {
                ResponseWriter = (context, report) => report.Status == HealthStatus.Healthy
                    ? context.Response.WriteAsJsonAsync(new ApiResponse<object>
                    {
                        Success = true,
                        Data = new { status = report.Status.ToString() }
                    })
                    : context.Response.WriteAsJsonAsync(new ApiResponse<object>
                    {
                        Success = false,
                        Message = "Dịch vụ tạm thời không khả dụng.",
                        ErrorCode = "SERVICE_UNAVAILABLE",
                        Data = null
                    })
            }).AllowAnonymous();
            return endpoints;
        }

        /// <summary>Opens a connection to the API database, bounded so a hung database fails the check quickly.</summary>
        private sealed class DatabaseHealthCheck(ANGIContext context) : IHealthCheck
        {
            private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

            public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext checkContext, CancellationToken ct)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(Timeout);
                try
                {
                    return await context.Database.CanConnectAsync(timeout.Token)
                        ? HealthCheckResult.Healthy()
                        : HealthCheckResult.Unhealthy("Database unreachable.");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return HealthCheckResult.Unhealthy("Database timed out.");
                }
            }
        }
    }
}
