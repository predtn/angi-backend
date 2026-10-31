using System.Net;
using Microsoft.Extensions.Configuration;

namespace ANGI.Infrastructure.Services.Ai;

/// <summary>Routes both backend AI clients through the shared durable spending boundary.</summary>
public sealed class AiBudgetGatewayHandler(IConfiguration configuration) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = request.Headers.Authorization;
        var baseUrl = configuration["AiBudgetGateway:BaseUrl"] ?? "http://127.0.0.1:18080/v1/";
        if (token?.Scheme != "Bearer" || token.Parameter is not { Length: >= 40 } key ||
            !key.StartsWith("angi-chat-", StringComparison.Ordinal) || key.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out var gateway) ||
            gateway.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(gateway.UserInfo) ||
            !string.IsNullOrEmpty(gateway.Query) || !string.IsNullOrEmpty(gateway.Fragment) ||
            !gateway.AbsolutePath.EndsWith("/v1/", StringComparison.Ordinal) ||
            request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.Ordinal) != true)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) {
                Content = new StringContent("{\"error\":{\"code\":\"ai_budget_gateway_required\",\"message\":\"Configure the scoped AI budget gateway credential.\"}}") });
        request.RequestUri = new Uri(gateway, "chat/completions");
        return base.SendAsync(request, cancellationToken);
    }
}
