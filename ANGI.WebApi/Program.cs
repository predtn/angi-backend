using ANGI.Application;
using ANGI.Infrastructure;
using ANGI.Infrastructure.Persistences;
using ANGI.WebApi;
using ANGI.WebApi.Configs;
using ANGI.WebApi.Middlewares;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    // One JSON object per line, so the host's log viewer (Render) can search by level, category and exception.
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(options =>
    {
        options.IncludeScopes = true;
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        options.UseUtcTimestamp = true;
    });
}

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddWebApi(builder.Configuration);

var app = builder.Build();

app.UseForwardedHeadersIfEnabled();
app.UseMiddleware<TrustedClientIpMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseApiStatusCodePages();

if (app.Environment.IsDevelopment())
{
    // Keep the local database in sync on every run; never in other environments
    await app.Services.MigrateDatabaseAsync();
}

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.UseCors(ANGI.WebApi.Configs.CorsConfig.PolicyName);
app.UseMiddleware<RateLimitPartitionMiddleware>();
app.UseMiddleware<LoginFailureLimitMiddleware>();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseMiddleware<AccountStatusMiddleware>();

app.MapControllers();
app.MapHealthCheckEndpoint();

app.Run();

/// <summary>Exposes the generated host to the HTTP integration-test project.</summary>
public partial class Program;
