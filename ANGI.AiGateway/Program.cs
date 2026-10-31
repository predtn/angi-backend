using ANGI.AiGateway;
using ANGI.AiGateway.Storage;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 262144);
var settings = builder.Configuration.GetSection("Gateway").Get<GatewaySettings>() ?? new();
settings.Validate();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<RequestPolicy>();
builder.Services.AddSingleton<PgBudgetLedger>();
builder.Services.AddSingleton<IBudgetLedger>(services => services.GetRequiredService<PgBudgetLedger>());
builder.Services.AddHttpClient<GatewayService>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
// Do not emit HttpClient request headers (provider credentials) or bodies in application logs.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
var app = builder.Build();
if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await app.Services.GetRequiredService<PgBudgetLedger>().InitializeAsync(CancellationToken.None);
    return;
}
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (Exception error)
    {
        if (context.Response.HasStarted) throw;
        var response = error is GatewayDenied denied ? GatewayService.Error(denied.Code, denied.Status)
            : GatewayService.Error(error is BadHttpRequestException or JsonException ? "gateway_invalid_request" : "gateway_ledger_unavailable",
                error is BadHttpRequestException or JsonException ? 400 : 503);
        context.Response.StatusCode = response.Status;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(response.Body);
    }
});

string Authenticate(HttpContext context, bool admin = false)
{
    var scope = settings.Authenticate(context.Request.Headers.Authorization.ToString());
    if (scope is null) throw new GatewayDenied("gateway_unauthorized", 401);
    if (admin != (scope == "admin")) throw new GatewayDenied("gateway_forbidden", 403);
    return scope;
}

async Task<byte[]> ReadRequest(HttpContext context)
{
    if (context.Request.QueryString.HasValue || !context.Request.HasJsonContentType()) throw new GatewayDenied("gateway_invalid_request", 400);
    var limit = settings.ChatMaxBodyBytes;
    using var output = new MemoryStream();
    var buffer = new byte[8192];
    int count;
    while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
    {
        if (output.Length + count > limit) throw new GatewayDenied("gateway_request_too_large", 413);
        output.Write(buffer, 0, count);
    }
    return output.ToArray();
}

app.MapGet("/health", async (PgBudgetLedger ledger, CancellationToken ct) =>
{
    await ledger.EnsureReadyAsync(ct);
    return Results.Ok(new { ledger = "ready" });
});
app.MapPost("/v1/chat/completions", async (HttpContext context, GatewayService gateway) =>
{
    var scope = Authenticate(context);
    var result = await gateway.ExecuteAsync(scope, await ReadRequest(context), context.RequestAborted);
    return Results.Content(System.Text.Encoding.UTF8.GetString(result.Body), "application/json", statusCode: result.Status);
});
app.MapPost("/v1/quote", async (HttpContext context, RequestPolicy policy) =>
{
    var scope = Authenticate(context);
    var request = policy.Prepare(scope, await ReadRequest(context));
    return Results.Ok(new { workload = scope, model = GatewaySettings.Model, request.ReservedUsd, request.MaxOutputTokens,
        inputTokenCeiling = GatewaySettings.ContextTokens, pricingVersion = GatewaySettings.PriceVersion, settings.PricingValidUntil });
});
var admin = app.MapGroup("/admin");
admin.AddEndpointFilter(async (invocation, next) => { Authenticate(invocation.HttpContext, true); return await next(invocation); });
admin.MapGet("/status", async (PgBudgetLedger ledger, CancellationToken ct) => Results.Ok(new {
    pricingVersion = GatewaySettings.PriceVersion, settings.PricingValidUntil, policyHash = settings.PolicyHash,
    state = await ledger.StatusAsync(ct) }));
admin.MapPost("/control", async (ControlChange change, PgBudgetLedger ledger, CancellationToken ct) => {
    await ledger.ChangeControlAsync(change, ct); return Results.NoContent(); });
admin.MapPut("/budgets/{workload}", async (string workload, BudgetChange change, PgBudgetLedger ledger, CancellationToken ct) => {
    await ledger.ChangeBudgetAsync(workload, change, ct); return Results.NoContent(); });
admin.MapPost("/reservations/{id:guid}/close-unknown", async (Guid id, PgBudgetLedger ledger, CancellationToken ct) => {
    await ledger.SettleAsync(new(id, "", 0), null, "abandoned", true, ct); return Results.NoContent(); });
await app.RunAsync();

public partial class Program;
