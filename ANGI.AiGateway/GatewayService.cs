using System.Net.Http.Headers;
using System.Text.Json;

namespace ANGI.AiGateway;

/// <summary>One admission and one upstream attempt. An uncertain outcome never releases money automatically.</summary>
public sealed class GatewayService(GatewaySettings settings, RequestPolicy policy, IBudgetLedger ledger, HttpClient provider)
{
    public async Task<GatewayResponse> ExecuteAsync(string workload, byte[] body, CancellationToken ct)
    {
        var request = policy.Prepare(workload, body);
        if (string.IsNullOrWhiteSpace(settings.ProviderApiKey) || settings.ProviderApiKey.Any(char.IsControl))
            throw new GatewayDenied("gateway_provider_not_configured");
        // A committed reservation is mandatory, even for retries from the caller's SDK.
        var reservation = await ledger.ReserveAsync(request, ct);
        byte[] responseBody;
        bool success;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ProviderApiKey);
            message.Content = new ByteArrayContent(request.Body);
            message.Content.Headers.ContentType = new("application/json");
            using var response = await provider.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            success = response.IsSuccessStatusCode;
            responseBody = success ? await ReadBoundedAsync(response.Content, 2_000_000, timeout.Token) : [];
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or GatewayDenied)
        {
            await SettleAsync(reservation, null, "unknown", true);
            return Error("gateway_upstream_outcome_unknown");
        }
        var usage = success ? RequestPolicy.ReadUsage(responseBody, request) : null;
        await SettleAsync(reservation, usage, !success ? "upstream_error" : usage is null ? "unknown" : "success", usage is null);
        return usage is null ? Error(success ? "gateway_usage_missing" : "gateway_upstream_rejected") : new(200, responseBody);
    }

    private async Task SettleAsync(Reservation reservation, CompletionUsage? usage, string outcome, bool stop)
    {
        // A disconnected client must not prevent accounting. Failure leaves the durable reservation pending.
        using var accounting = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await ledger.SettleAsync(reservation, usage, outcome, stop, accounting.Token); }
        catch { throw new GatewayDenied("gateway_settlement_unavailable"); }
    }

    public static GatewayResponse Error(string code, int status = 503) => new(status,
        JsonSerializer.SerializeToUtf8Bytes(new { error = new { message = "AI request blocked by spending controls.", type = "budget_control", code } }), code);

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) throw new GatewayDenied("gateway_response_too_large");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (result.Length + read > maximum) throw new GatewayDenied("gateway_response_too_large");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }
}
