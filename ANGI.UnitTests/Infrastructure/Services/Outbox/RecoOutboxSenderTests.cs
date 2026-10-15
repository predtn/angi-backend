using System.Net;
using System.Text;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Services.Outbox;
using ANGI.Infrastructure.Services.Recommendation;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace ANGI.UnitTests.Infrastructure.Services.Outbox;

public sealed class RecoOutboxSenderTests
{
    private static readonly Guid EventUuid = Guid.Parse("6f0c2b1e-8d7a-4c1e-9b3f-2a5d7e9c1f00");

    private static (RecoOutboxSender Sender, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://reco.test/") };
        var settings = Options.Create(new RecommendationSettings { BaseUrl = "http://reco.test/", ApiKey = "test-key" });
        return (new RecoOutboxSender(client, settings), handler);
    }

    private static OutboxMessage Message(OutboxEventType type, string payload) => new()
    {
        Id = 1,
        EventUuid = EventUuid,
        EventType = type,
        AggregateKey = "dish:105",
        Payload = payload
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // TEST-01: Each event type goes to its RECO endpoint with the payload as body and both headers.
    [Theory]
    [InlineData(OutboxEventType.DishUpserted, """{"dish_id":105,"name":"Bún bò"}""", "POST", "http://reco.test/dishes/upsert", true)]
    [InlineData(OutboxEventType.FeedbackCreated, """{"event_uuid":"x","dish_id":105}""", "POST", "http://reco.test/feedback", true)]
    [InlineData(OutboxEventType.DishDeactivated, """{"dish_id":105,"occurred_at":"2026-10-02T02:30:00.000000Z"}""", "DELETE",
        "http://reco.test/dishes/105?occurred_at=2026-10-02T02%3A30%3A00.000000Z", false)]
    public async Task SendAsync_ShouldCallTheDocumentedEndpoint(
        OutboxEventType type, string payload, string method, string url, bool hasBody)
    {
        var (sender, handler) = Create(_ => Json(HttpStatusCode.OK, "{}"));

        var result = await sender.SendAsync(Message(type, payload), CancellationToken.None);

        result.Outcome.Should().Be(OutboxDeliveryOutcome.Delivered);
        handler.Method.Should().Be(method);
        handler.Url.Should().Be(url);
        handler.Headers["X-Api-Key"].Should().Be("test-key");
        handler.Headers["Idempotency-Key"].Should().Be(EventUuid.ToString());
        if (hasBody)
        {
            handler.Body.Should().Be(payload);
        }
    }

    // TEST-02: 4xx is dead with the reco error code; 5xx is retried, with Retry-After when reco sends it.
    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"error_code":"VALIDATION_FAILED"}""", OutboxDeliveryOutcome.Dead, "HTTP 422 VALIDATION_FAILED")]
    [InlineData(HttpStatusCode.NotFound, """{"error_code":"DISH_NOT_FOUND"}""", OutboxDeliveryOutcome.Dead, "HTTP 404 DISH_NOT_FOUND")]
    [InlineData(HttpStatusCode.Unauthorized, "not json", OutboxDeliveryOutcome.Dead, "HTTP 401")]
    [InlineData(HttpStatusCode.InternalServerError, "{}", OutboxDeliveryOutcome.Retry, "HTTP 500")]
    public async Task SendAsync_ShouldClassifyErrorResponses(
        HttpStatusCode status, string body, OutboxDeliveryOutcome outcome, string error)
    {
        var (sender, _) = Create(_ => Json(status, body));

        var result = await sender.SendAsync(Message(OutboxEventType.DishUpserted, "{}"), CancellationToken.None);

        result.Outcome.Should().Be(outcome);
        result.Error.Should().Be(error);
    }

    [Fact]
    public async Task SendAsync_With503RetryAfter_ShouldRetryAfterThatDelay()
    {
        var (sender, _) = Create(_ =>
        {
            var response = Json(HttpStatusCode.ServiceUnavailable, """{"error_code":"DISH_VECTOR_NOT_READY"}""");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });

        var result = await sender.SendAsync(Message(OutboxEventType.FeedbackCreated, "{}"), CancellationToken.None);

        result.Should().Be(new OutboxDeliveryResult(OutboxDeliveryOutcome.Retry, "HTTP 503 DISH_VECTOR_NOT_READY", TimeSpan.FromSeconds(30)));
    }

    // TEST-03: Timeouts and network errors are retried; an unreadable payload is dead without any call.
    [Fact]
    public async Task SendAsync_WithTimeoutOrNetworkError_ShouldRetry()
    {
        var (timeout, _) = Create(_ => throw new TaskCanceledException("timeout"));
        var (network, _) = Create(_ => throw new HttpRequestException("connection refused"));

        (await timeout.SendAsync(Message(OutboxEventType.DishUpserted, "{}"), CancellationToken.None))
            .Should().Be(new OutboxDeliveryResult(OutboxDeliveryOutcome.Retry, "Timeout"));
        (await network.SendAsync(Message(OutboxEventType.DishUpserted, "{}"), CancellationToken.None))
            .Outcome.Should().Be(OutboxDeliveryOutcome.Retry);
    }

    [Theory]
    [InlineData(OutboxEventType.DishUpserted, "{not json")]
    [InlineData(OutboxEventType.DishDeactivated, """{"occurred_at":"x"}""")]
    public async Task SendAsync_WithUnreadablePayload_ShouldBeDeadWithoutCalling(OutboxEventType type, string payload)
    {
        var (sender, handler) = Create(_ => Json(HttpStatusCode.OK, "{}"));

        var result = await sender.SendAsync(Message(type, payload), CancellationToken.None);

        result.Outcome.Should().Be(OutboxDeliveryOutcome.Dead);
        handler.Method.Should().BeNull();
    }

    // TEST-04: Backoff doubles from 5 s and stops at 15 min.
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(5, 80)]
    [InlineData(9, 900)]
    public void Backoff_ShouldDoubleUpToFifteenMinutes(int attempts, int seconds)
    {
        OutboxDispatcher.Backoff(attempts).Should().Be(TimeSpan.FromSeconds(seconds));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? Method { get; private set; }
        public string? Url { get; private set; }
        public string? Body { get; private set; }
        public Dictionary<string, string> Headers { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method.Method;
            Url = request.RequestUri!.AbsoluteUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            foreach (var header in request.Headers)
            {
                Headers[header.Key] = string.Join(",", header.Value);
            }

            return respond(request);
        }
    }
}
