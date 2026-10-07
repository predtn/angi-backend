using ANGI.Application;
using ANGI.Infrastructure;
using ANGI.Infrastructure.Persistences;
using ANGI.WebApi;
using ANGI.WebApi.Configs;
using ANGI.WebApi.Middlewares;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddWebApi(builder.Configuration);

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseApiStatusCodePages();

if (app.Environment.IsDevelopment())
{
    // Keep the local database in sync on every run; never in other environments
    await app.Services.MigrateDatabaseAsync();

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

app.MapControllers();

app.Run();
