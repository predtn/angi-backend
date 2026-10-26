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
        sent.RootElement.GetProperty("tools").GetArrayLength().Should().Be(3);
        sent.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean().Should().BeTrue();
        body.Should().NotContain("test-key");
    }

    // TEST-02: Reject malformed JSON, missing fields, extra fields, invalid enums, refusal and truncated responses.
    /// <summary>Validates every provider-output boundary before Application receives an actionable step.</summary>
    [Theory]
    [InlineData("json")][InlineData("missing")][InlineData("extra")][InlineData("enum")][InlineData("duplicate")]
    [InlineData("refusal")][InlineData("truncated")][InlineData("http")][InlineData("choices")]
    public async Task RunAsync_InvalidProviderResponse_ReturnsControlledFailure(string area)
    {
        var output = area switch { "json" => "{", "missing" => "{}", "extra" => Output().TrimEnd('}') + ",\"userId\":99}",
            "enum" => Output().Replace("answer", "delete_roadmap"), "duplicate" => Output().TrimEnd('}') + ",\"action\":\"answer\"}", _ => Output() };
        var handler = new Handler(_ => Task.FromResult(area == "http" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) :
            area == "choices" ? Response(new { choices = Array.Empty<object>() }) : Response(new
            {
                choices = new[] { new { finish_reason = area == "truncated" ? "length" : "stop", message = new { content = output, refusal = area == "refusal" ? "refused" : null } } }
            })));
        (await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None)).Should().BeNull();
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
        (await service.RunAsync(AgentTestData.Context(), false, CancellationToken.None)).Should().BeNull(); handler.Calls.Should().Be(0);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var act = () => Service(handler).RunAsync(AgentTestData.Context(), false, cancelled.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Creates the strict response object while keeping every nullable field explicit.</summary>
    private static string Output() => JsonSerializer.Serialize(new { action = "answer", reply = "Explanation", state = (object?)null,
        constraintsChangeConfirmed = false, baseProposalVersion = (int?)null, scope = new { dayNumber = (short?)null, mealSlot = (string?)null }, items = Array.Empty<object>(), changes = Array.Empty<object>() });
    /// <summary>Creates a typed provider client configured with deterministic non-secret credentials.</summary>
    private static OpenAiAgentLlmService Service(HttpMessageHandler handler, string key = "test-key") => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/v1/") }, Options.Create(new AiSettings { ApiKey = key, Model = "configured-model" }), new AgentLimits());
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
