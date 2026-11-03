using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Services.Recommendation;
using Microsoft.Extensions.Options;

namespace ANGI.Infrastructure.Services.Outbox;

/// <summary>
/// Sends outbox messages to reco: DISH_UPSERTED → RECO-01, DISH_DEACTIVATED → RECO-02, FEEDBACK_CREATED → RECO-04,
/// each with X-Api-Key and Idempotency-Key = event_uuid. The trigger already wrote each payload as its request body.
/// </summary>
public sealed class RecoOutboxSender : IOutboxSender
{
    private readonly HttpClient _httpClient;
    private readonly RecommendationSettings _settings;

    public RecoOutboxSender(HttpClient httpClient, IOptions<RecommendationSettings> settings)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
    }

    public bool IsConfigured => _httpClient.BaseAddress is not null && !string.IsNullOrWhiteSpace(_settings.ApiKey);

    public async Task<OutboxDeliveryResult> SendAsync(OutboxMessage message, CancellationToken ct)
    {
        HttpRequestMessage request;
        try
        {
            request = BuildRequest(message);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new OutboxDeliveryResult(OutboxDeliveryOutcome.Dead, $"Unreadable payload: {exception.Message}");
        }

        using (request)
        {
            try
            {
                using var response = await _httpClient.SendAsync(request, ct);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    return OutboxDeliveryResult.Delivered;
                }

                var error = $"HTTP {status} {await ReadErrorCodeAsync(response, ct)}".TrimEnd();
                return status >= 500
                    ? new OutboxDeliveryResult(OutboxDeliveryOutcome.Retry, error, RetryAfter(response.Headers.RetryAfter))
                    : new OutboxDeliveryResult(OutboxDeliveryOutcome.Dead, error);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return new OutboxDeliveryResult(OutboxDeliveryOutcome.Retry, "Timeout");
            }
            catch (HttpRequestException exception)
            {
                return new OutboxDeliveryResult(OutboxDeliveryOutcome.Retry, $"Network error: {exception.Message}");
            }
        }
    }

    private HttpRequestMessage BuildRequest(OutboxMessage message)
    {
        var request = message.EventType switch
        {
            OutboxEventType.DishUpserted => JsonPost("dishes/upsert", message.Payload),
            OutboxEventType.DishDeactivated => Deactivate(message.Payload),
            OutboxEventType.FeedbackCreated => JsonPost("feedback", message.Payload),
            OutboxEventType.AgentImpressionRecorded => JsonPost("impressions", message.Payload),
            _ => throw new InvalidOperationException($"Unknown event type {message.EventType}.")
        };
        request.Headers.Add("X-Api-Key", _settings.ApiKey);
        request.Headers.Add("Idempotency-Key", message.EventUuid.ToString());
        return request;
    }

    private static HttpRequestMessage JsonPost(string path, string payload)
    {
        using var _ = JsonDocument.Parse(payload);
        return new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>RECO-02 takes the dish id in the path and occurred_at in the query string.</summary>
    private static HttpRequestMessage Deactivate(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var dishId = document.RootElement.GetProperty("dish_id").GetInt32();
        var path = $"dishes/{dishId}";
        if (document.RootElement.TryGetProperty("occurred_at", out var occurredAt) && occurredAt.ValueKind == JsonValueKind.String)
        {
            path += $"?occurred_at={Uri.EscapeDataString(occurredAt.GetString()!)}";
        }

        return new HttpRequestMessage(HttpMethod.Delete, path);
    }

    private static TimeSpan? RetryAfter(RetryConditionHeaderValue? header) =>
        header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

    private static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.TryGetProperty("error_code", out var code) ? code.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
