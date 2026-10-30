using System.Net;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Models.Agent;
using ANGI.Infrastructure.Services.Agent;
using ANGI.Infrastructure.Services.Ai;
using ANGI.UnitTests.Application.Services.Agent;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace ANGI.UnitTests.Infrastructure.Services.Agent;

/// <summary>Verifies the real provider transport, strict schemas, tool associations and malformed-output handling.</summary>
public sealed class OpenAiAgentLlmServiceTests
{
    [Fact]
    public async Task RunAsync_ToolBudgetExhausted_ForcesFinalStructuredOutput()
    {
        var handler = new Handler(async request =>
        {
            using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            sent.RootElement.GetProperty("tool_choice").GetString().Should().Be("none");
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });
        var context = AgentTestData.Context();
        for (var round = 0; round < 3; round++) context.Exchanges.Add(new([], []));
        (await Service(handler).RunAsync(context, false, CancellationToken.None))!.Output!.Action.Should().Be("answer");
    }

    // TEST-01: Send allowlisted strict tool schemas and parse a complete structured answer with token usage.
    /// <summary>Verifies the exact HTTP authentication and structured-output contract without a live API key.</summary>
    [Fact]
    public async Task RunAsync_StrictAnswer_UsesExpectedProviderContract()
    {
        string? body = null;
        var output = Output();
        var handler = new Handler(async request =>
        {
            request.Headers.Authorization!.Scheme.Should().Be("Bearer"); request.Headers.Authorization.Parameter.Should().Be("test-key");
            request.RequestUri!.AbsolutePath.Should().Be("/v1/chat/completions");
            body = await request.Content!.ReadAsStringAsync();
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = output, refusal = (string?)null } } }, usage = new { total_tokens = 123 } });
        });
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Output!.Action.Should().Be("answer"); result.Tokens.Should().Be(123);
        using var sent = JsonDocument.Parse(body!);
        sent.RootElement.GetProperty("tools").GetArrayLength().Should().Be(4);
        sent.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean().Should().BeTrue();
        body.Should().NotContain("test-key");
    }

    // TEST-02: Reject malformed JSON, missing fields, extra fields, invalid enums, refusal and truncated responses.
    /// <summary>Validates every provider-output boundary before Application receives an actionable step.</summary>
    [Theory]
    [InlineData("json", AgentLlmFailureKind.InvalidSchema)][InlineData("missing", AgentLlmFailureKind.InvalidSchema)]
    [InlineData("extra", AgentLlmFailureKind.InvalidSchema)][InlineData("enum", AgentLlmFailureKind.InvalidSchema)][InlineData("duplicate", AgentLlmFailureKind.InvalidSchema)]
    [InlineData("refusal", AgentLlmFailureKind.Refused)][InlineData("truncated", AgentLlmFailureKind.Truncated)]
    [InlineData("http", AgentLlmFailureKind.ProviderUnavailable)][InlineData("choices", AgentLlmFailureKind.InvalidResponse)]
    public async Task RunAsync_InvalidProviderResponse_ReturnsControlledFailure(string area, AgentLlmFailureKind kind)
    {
        var output = area switch { "json" => "{", "missing" => "{}", "extra" => Output().TrimEnd('}') + ",\"userId\":99}",
            "enum" => Output().Replace("answer", "delete_roadmap"), "duplicate" => Output().TrimEnd('}') + ",\"action\":\"answer\"}", _ => Output() };
        var handler = new Handler(_ => Task.FromResult(area == "http" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) :
            area == "choices" ? Response(new { choices = Array.Empty<object>() }) : Response(new
            {
                choices = new[] { new { finish_reason = area == "truncated" ? "length" : "stop", message = new { content = output, refusal = area == "refusal" ? "refused" : null } } }
            })));
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Failure!.Kind.Should().Be(kind); result.Output.Should().BeNull(); result.Tools.Should().BeEmpty();
        handler.Calls.Should().Be(1);
    }

    // TEST-03: Preserve provider tool call IDs and parse only allowlisted strictly validated arguments.
    /// <summary>Verifies tool-call/result correlation needed by subsequent provider rounds.</summary>
    [Fact]
    public async Task RunAsync_ToolCall_PreservesProviderIdentity()
    {
        var handler = new Handler(_ => Task.FromResult(Response(new { choices = new[] { new { finish_reason = "tool_calls", message = new
        { tool_calls = new[] { new { id = "call-owned", type = "function", function = new { name = "get_roadmap", arguments = "{\"roadmapId\":88}" } } } } } } })));
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Tools.Single().Id.Should().Be("call-owned"); result.Tools.Single().Name.Should().Be("get_roadmap");
    }

    // TEST-04: Missing provider credentials avoid HTTP calls and caller cancellation is never converted to fallback.
    /// <summary>Verifies configuration and cancellation boundaries.</summary>
    [Fact]
    public async Task RunAsync_NoCredentialsOrCancellation_RespectsBoundary()
    {
        var handler = new Handler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct));
        var service = Service(handler, "");
        (await service.RunAsync(AgentTestData.Context(), false, CancellationToken.None))!.Failure!.Kind.Should().Be(AgentLlmFailureKind.Configuration); handler.Calls.Should().Be(0);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var act = () => Service(handler).RunAsync(AgentTestData.Context(), false, cancelled.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // TEST-05: Gemini requires its opaque tool signature on the subsequent assistant message.
    /// <summary>Checks a real parse/replay boundary and keeps provider metadata out of stored tool transcripts.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_ToolRoundTrip_PreservesOptionalProviderMetadata(bool signed)
    {
        var call = new Dictionary<string, object?> { ["id"] = "call-signed", ["type"] = "function",
            ["function"] = new { name = "get_roadmap", arguments = "{\"roadmapId\":88}" } };
        if (signed) call["extra_content"] = new { google = new { thought_signature = "opaque-provider-signature" } };
        var handler = new Handler(async request =>
        {
            if (request.Content is not null)
            {
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
                var messages = body.RootElement.GetProperty("messages");
                if (messages.GetArrayLength() > 2)
                {
                    var replay = messages[2].GetProperty("tool_calls")[0];
                    replay.GetProperty("id").GetString().Should().Be("call-signed");
                    replay.TryGetProperty("extra_content", out var metadata).Should().Be(signed);
                    if (signed) metadata.GetProperty("google").GetProperty("thought_signature").GetString().Should().Be("opaque-provider-signature");
                    messages[3].GetProperty("tool_call_id").GetString().Should().Be("call-signed");
                    return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
                }
            }
            return Response(new { choices = new[] { new { finish_reason = "tool_calls", message = new { tool_calls = new[] { call } } } } });
        });
        var service = Service(handler);
        var context = AgentTestData.Context();
        var first = await service.RunAsync(context, false, CancellationToken.None);
        first.Should().NotBeNull();
        first!.Tools.Single().ProviderExtraContent.HasValue.Should().Be(signed);
        AgentJson.Write(first.Tools).Should().NotContain("opaque-provider-signature").And.NotContain("providerExtraContent");
        context.Exchanges.Add(new(first.Tools, [JsonSerializer.SerializeToElement(new { roadmapId = 88 })]));
        var second = await service.RunAsync(context, false, CancellationToken.None);
        second!.Output!.Action.Should().Be("answer");
        handler.Calls.Should().Be(2);
    }

    // TEST-06: Escaped Unicode in nested JSON can make a provider return literal escape sequences as province names.
    /// <summary>Preserves readable Vietnamese inside both context and tool data messages without changing persisted JSON.</summary>
    [Fact]
    public async Task RunAsync_VietnameseContextAndToolResult_RemainReadable()
    {
        var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var messages = body.RootElement.GetProperty("messages");
            messages[1].GetProperty("content").GetString().Should().Contain("Hà Nội");
            messages[3].GetProperty("content").GetString().Should().Contain("Phở bò Hà Nội");
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });
        var context = AgentTestData.Context();
        context.State = context.State with { Province = "Hà Nội" };
        context.Exchanges.Add(new([new("call-vi", "get_roadmap", JsonSerializer.SerializeToElement(new { roadmapId = 88 }))],
            [JsonSerializer.SerializeToElement(new { dishName = "Phở bò Hà Nội" }, AgentJson.Options)]));
        (await Service(handler).RunAsync(context, false, CancellationToken.None))!.Output!.Action.Should().Be("answer");
    }

    // TEST-07: Preserve safe HTTP status and Retry-After without reading or exposing a provider error body.
    /// <summary>Distinguishes quota, authentication, rejected requests and unavailable services with one transport attempt.</summary>
    [Theory]
    [InlineData(429, AgentLlmFailureKind.RateLimited)]
    [InlineData(401, AgentLlmFailureKind.Authentication)][InlineData(403, AgentLlmFailureKind.Authentication)]
    [InlineData(400, AgentLlmFailureKind.RejectedRequest)]
    [InlineData(500, AgentLlmFailureKind.ProviderUnavailable)][InlineData(503, AgentLlmFailureKind.ProviderUnavailable)]
    [InlineData(408, AgentLlmFailureKind.Timeout)][InlineData(504, AgentLlmFailureKind.Timeout)]
    public async Task RunAsync_HttpFailure_PreservesOnlySafeMetadata(int status, AgentLlmFailureKind kind)
    {
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private-key-and-prompt-must-not-leak") };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(45)); return Task.FromResult(response);
        });
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Failure.Should().Be(new AgentLlmFailure(kind, status, 45));
        AgentJson.Write(result).Should().NotContain("private-key").And.NotContain("test-key"); handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_GeminiHighDemand_PreservesSafeReasonWithoutRawMessage()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        { Content = new StringContent("{\"error\":{\"status\":\"UNAVAILABLE\",\"message\":\"This model is experiencing high demand. private-key-must-not-leak\"}}") }));
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Failure!.ProviderCode.Should().Be("UNAVAILABLE"); result.Failure.Reason.Should().Be("high_demand");
        AgentJson.Write(result).Should().NotContain("private-key");
    }

    [Theory]
    [InlineData("[{}]")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"private-message\"")]
    public async Task RunAsync_UnusualErrorEnvelope_PreservesHttp429(string body)
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(body) }));
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Failure!.Kind.Should().Be(AgentLlmFailureKind.RateLimited);
        result.Failure.HttpStatus.Should().Be(429);
    }

    [Fact]
    public async Task RunAsync_GeminiArrayRateLimit_ReadsStatusAndRetryInfo()
    {
        var body = "[{\"error\":{\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\",\"message\":\"Quota exceeded: GenerateRequestsPerMinutePerProject\",\"details\":[{\"retryDelay\":\"12.5s\"}]}}]";
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(body) }));
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Failure.Should().Be(new AgentLlmFailure(AgentLlmFailureKind.RateLimited, 429, 13, "RESOURCE_EXHAUSTED", "rate_limit"));
    }

    // TEST-08: HttpClient timeout and a transport exception remain distinct from received malformed output.
    /// <summary>Exercises cancellation-aware fake transport without making any network request.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task RunAsync_TransportFailure_IsNotSchemaFailure(bool timeout)
    {
        var handler = new Handler(async (_, ct) =>
        {
            if (timeout) await Task.Delay(Timeout.Infinite, ct);
            throw new HttpRequestException("sensitive internal endpoint");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/v1/"), Timeout = TimeSpan.FromMilliseconds(100) };
        var service = new OpenAiAgentLlmService(http, Options.Create(new AiSettings { ApiKey = "test-key", Model = "test-model" }), new(), TimeProvider.System);
        var result = await service.RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Failure!.Kind.Should().Be(timeout ? AgentLlmFailureKind.Timeout : AgentLlmFailureKind.Network);
        AgentJson.Write(result).Should().NotContain("sensitive"); handler.Calls.Should().Be(1);
    }

    // TEST-09: Date-based retry hints use the injected clock and malformed or expired hints are ignored.
    /// <summary>Checks deterministic retry guidance without weakening HTTP failure classification.</summary>
    [Theory]
    [InlineData("future", 30)][InlineData("past", null)][InlineData("invalid", null)]
    public async Task RunAsync_RetryAfter_UsesSafeClockBasedDelay(string hint, int? expected)
    {
        var clock = new FixedClock();
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (hint == "invalid") response.Headers.TryAddWithoutValidation("Retry-After", "not-a-date");
            else response.Headers.RetryAfter = new(clock.GetUtcNow().AddSeconds(hint == "future" ? 30 : -30));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/v1/") };
        var service = new OpenAiAgentLlmService(http, Options.Create(new AiSettings { ApiKey = "test-key", Model = "test-model" }), new(), clock);
        var result = await service.RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Failure!.Kind.Should().Be(AgentLlmFailureKind.RateLimited); result.Failure.RetryAfterSeconds.Should().Be(expected);
    }

    // TEST-10: An invalid provider envelope retains HTTP 200 but is not misclassified as received model content needing repair.
    /// <summary>Distinguishes protocol corruption from malformed JSON produced inside a valid model response.</summary>
    [Fact]
    public async Task RunAsync_InvalidEnvelope_PreservesStatusWithoutSchemaRepair()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not-valid-provider-json") }));
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Failure.Should().Be(new AgentLlmFailure(AgentLlmFailureKind.InvalidResponse, 200)); handler.Calls.Should().Be(1);
    }

    /// <summary>Supplies an exact clock for HTTP-date retry guidance.</summary>
    private sealed class FixedClock : TimeProvider
    {
        /// <summary>Returns the deterministic instant used to interpret provider HTTP dates.</summary>
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Creates the strict response object while keeping every nullable field explicit.</summary>
    private static string Output() => JsonSerializer.Serialize(new { action = "answer", reply = "Explanation", state = (object?)null,
        clarification = (string?)null,
        constraintsChangeConfirmed = false, baseProposalVersion = (int?)null, scope = new { dayNumber = (short?)null, mealSlot = (string?)null }, items = Array.Empty<object>(), changes = Array.Empty<object>() });
    /// <summary>Creates a typed provider client configured with deterministic non-secret credentials.</summary>
    private static OpenAiAgentLlmService Service(HttpMessageHandler handler, string key = "test-key") => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/v1/") }, Options.Create(new AiSettings { ApiKey = key, Model = "configured-model" }), new AgentLimits(), TimeProvider.System);
    /// <summary>Encodes an OpenAI-compatible test response as JSON.</summary>
    private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    /// <summary>Provides deterministic HTTP responses without a live provider call.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        public int Calls;
        /// <summary>Adapts a request-only responder.</summary>
        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => _send = (request, _) => send(request);
        /// <summary>Adapts a cancellation-aware responder.</summary>
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
        /// <summary>Returns the scripted response while recording transport use.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return _send(request, cancellationToken); }
    }
}
